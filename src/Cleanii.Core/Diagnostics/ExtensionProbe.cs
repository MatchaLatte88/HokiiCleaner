using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cleanii.Core.Diagnostics;

/// <summary>Liest Erweiterungen des Explorer-Kontextmenüs aus der Registry. Rein lesend.</summary>
internal static class ShellExtensionProbe
{
    private static readonly (string Scope, string Key)[] Locations =
    {
        ("Dateien", @"*\shellex\ContextMenuHandlers"),
        ("Alle Dateisystem-Objekte", @"AllFilesystemObjects\shellex\ContextMenuHandlers"),
        ("Ordner", @"Directory\shellex\ContextMenuHandlers"),
        ("Ordner-Hintergrund", @"Directory\Background\shellex\ContextMenuHandlers"),
        ("Ordner (allgemein)", @"Folder\shellex\ContextMenuHandlers"),
        ("Laufwerke", @"Drive\shellex\ContextMenuHandlers"),
    };

    public static IReadOnlyList<ShellExtensionInfo> Read()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        var result = new List<ShellExtensionInfo>();

        using RegistryKey classes = Registry.ClassesRoot;
        foreach ((string scope, string key) in Locations)
        {
            using RegistryKey? handlers = classes.OpenSubKey(key);
            if (handlers is null)
            {
                continue;
            }

            foreach (string handlerName in handlers.GetSubKeyNames())
            {
                using RegistryKey? handler = handlers.OpenSubKey(handlerName);
                string? clsid = handler?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(clsid) && Guid.TryParse(handlerName, out _))
                {
                    clsid = handlerName;
                }

                if (string.IsNullOrWhiteSpace(clsid))
                {
                    continue; // kein Verweis auf eine Komponente: lädt nichts
                }

                clsid = clsid.Trim();
                using RegistryKey? component = classes.OpenSubKey($@"CLSID\{clsid}");
                using RegistryKey? inproc = component?.OpenSubKey("InprocServer32");
                string? library = inproc?.GetValue(null) as string;
                library = string.IsNullOrWhiteSpace(library)
                    ? null
                    : Environment.ExpandEnvironmentVariables(library.Trim().Trim('"'));

                string name = (component?.GetValue(null) as string) is { Length: > 0 } friendly ? friendly : handlerName;
                bool windowsComponent = library is not null && library.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
                result.Add(new ShellExtensionInfo(scope, name, clsid, library, windowsComponent));
            }
        }

        return result;
    }
}

/// <summary>
/// Zählt installierte Browser-Erweiterungen: bei Chrome, Edge, Brave und Vivaldi aus den Profilordnern (auch
/// deaktivierte), bei Firefox die aktiven, vom Benutzer installierten. Rein lesend.
/// </summary>
internal static partial class BrowserExtensionProbe
{
    private static readonly (string Browser, string Root)[] Chromium =
    {
        ("Chrome", @"Google\Chrome\User Data"),
        ("Edge", @"Microsoft\Edge\User Data"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data"),
        ("Vivaldi", @"Vivaldi\User Data"),
    };

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ChromiumId();

    [GeneratedRegex(@"^__MSG_(.+)__$")]
    private static partial Regex LocalizedName();

    public static IReadOnlyList<BrowserExtensionInfo> Read()
    {
        var result = new List<BrowserExtensionInfo>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach ((string browser, string root) in Chromium)
        {
            string userData = Path.Combine(local, root);
            if (Directory.Exists(userData))
            {
                ReadChromium(result, browser, userData);
            }
        }

        string firefox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            ReadFirefox(result, firefox);
        }

        return result;
    }

    private static void ReadChromium(List<BrowserExtensionInfo> into, string browser, string userData)
    {
        foreach (string profile in Directory.EnumerateDirectories(userData))
        {
            string profileName = Path.GetFileName(profile);
            if (profileName != "Default" && !profileName.StartsWith("Profile ", StringComparison.Ordinal))
            {
                continue;
            }

            string extensions = Path.Combine(profile, "Extensions");
            if (!Directory.Exists(extensions))
            {
                continue;
            }

            foreach (string idFolder in Directory.EnumerateDirectories(extensions))
            {
                string id = Path.GetFileName(idFolder);
                if (!ChromiumId().IsMatch(id))
                {
                    continue; // z. B. der Ordner "Temp"
                }

                string? version = Directory.EnumerateDirectories(idFolder)
                    .OrderByDescending(folder => Path.GetFileName(folder), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                into.Add(new BrowserExtensionInfo(browser, profileName, version is null ? id : ChromiumName(version, id), id));
            }
        }
    }

    private static string ChromiumName(string versionFolder, string id)
    {
        string manifestPath = Path.Combine(versionFolder, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return $"{id} (ohne Manifest)";
        }

        try
        {
            using JsonDocument manifest = ReadJson(manifestPath);
            string? name = manifest.RootElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return id;
            }

            Match localized = LocalizedName().Match(name);
            if (!localized.Success)
            {
                return name;
            }

            string locale = manifest.RootElement.TryGetProperty("default_locale", out JsonElement l) ? l.GetString() ?? "en" : "en";
            string messages = Path.Combine(versionFolder, "_locales", locale, "messages.json");
            if (File.Exists(messages))
            {
                using JsonDocument texts = ReadJson(messages);
                foreach (JsonProperty entry in texts.RootElement.EnumerateObject())
                {
                    if (string.Equals(entry.Name, localized.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                        && entry.Value.TryGetProperty("message", out JsonElement message)
                        && message.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }

            return id;
        }
        catch (JsonException)
        {
            return $"{id} (Manifest nicht lesbar)";
        }
    }

    private static void ReadFirefox(List<BrowserExtensionInfo> into, string profiles)
    {
        foreach (string profile in Directory.EnumerateDirectories(profiles))
        {
            string file = Path.Combine(profile, "extensions.json");
            if (!File.Exists(file))
            {
                continue;
            }

            string profileName = Path.GetFileName(profile);
            try
            {
                using JsonDocument document = ReadJson(file);
                if (!document.RootElement.TryGetProperty("addons", out JsonElement addons))
                {
                    continue;
                }

                foreach (JsonElement addon in addons.EnumerateArray())
                {
                    bool isExtension = addon.TryGetProperty("type", out JsonElement type) && type.GetString() == "extension";
                    bool active = addon.TryGetProperty("active", out JsonElement a) && a.ValueKind == JsonValueKind.True;
                    bool userInstalled = addon.TryGetProperty("location", out JsonElement location) && location.GetString() == "app-profile";
                    if (!isExtension || !active || !userInstalled)
                    {
                        continue;
                    }

                    string id = addon.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? "?" : "?";
                    string name = addon.TryGetProperty("defaultLocale", out JsonElement locale)
                        && locale.TryGetProperty("name", out JsonElement n) && n.GetString() is { Length: > 0 } text
                            ? text
                            : id;
                    into.Add(new BrowserExtensionInfo("Firefox", profileName, name, id));
                }
            }
            catch (JsonException)
            {
                into.Add(new BrowserExtensionInfo("Firefox", profileName, "extensions.json nicht lesbar", "?"));
            }
        }
    }

    /// <summary>Liest eine Datei, die der Browser gerade geöffnet haben kann (Lesen ohne Sperre).</summary>
    private static JsonDocument ReadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }
}
