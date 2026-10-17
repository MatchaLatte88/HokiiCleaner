using System.Globalization;
using Cleanii.Core.Scanning;
using Microsoft.Win32;

namespace Cleanii.Core.Programs;

/// <summary>Ein Eintrag aus der Liste der installierten Programme (Registry, Schlüssel "Uninstall").</summary>
/// <param name="InstallLocation">Installationsordner; <c>null</c>, wenn die Registry keinen nennt und keiner ableitbar ist.</param>
/// <param name="LocationInferred">True, wenn der Ordner aus dem Pfad des Symbols abgeleitet wurde statt aus <c>InstallLocation</c>.</param>
/// <param name="EstimatedSizeBytes">Die vom Installer eingetragene Größe; viele Programme tragen keine ein.</param>
/// <param name="UninstallString">Der Befehl, den Windows zum Deinstallieren startet (Registry-Wert <c>UninstallString</c>); <c>null</c>, wenn keiner eingetragen ist.</param>
public sealed record InstalledProgram(
    string Name,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    bool LocationInferred,
    long? EstimatedSizeBytes,
    DateTime? InstallDate,
    string Source,
    string? UninstallString = null);

/// <summary>
/// Zerlegt den Deinstallations-Befehl aus der Registry in Programm und Argumente. Erkannt werden nur Befehle, die eine
/// <c>.exe</c> starten (auch <c>MsiExec.exe /X{...}</c>); alles andere (Skripte, <c>rundll32</c>-Ketten, Shell-Befehle) wird
/// abgelehnt, statt geraten zu werden.
/// </summary>
public static class UninstallCommand
{
    public static bool TryParse(string? command, out string file, out string arguments)
    {
        file = string.Empty;
        arguments = string.Empty;
        string text = (command ?? string.Empty).Trim();
        if (text.Length == 0 || text.Contains('\0'))
        {
            return false;
        }

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            if (close < 2)
            {
                return false;
            }

            file = text.Substring(1, close - 1);
            arguments = text.Substring(close + 1).Trim();
        }
        else
        {
            // Ohne Anführungszeichen endet der Programmpfad bei der ersten ".exe" (der Pfad darf Leerzeichen enthalten).
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0)
            {
                return false;
            }

            file = text.Substring(0, exe + 4);
            arguments = text.Substring(exe + 4).Trim();
        }

        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && (Path.IsPathFullyQualified(file) || !file.Contains('\\'))
            && !file.Contains(" /")
            && file.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
}

/// <summary>Ein Programm mit der Größe, die Cleanii im Scan gemessen hat (oder der Schätzung des Installers).</summary>
/// <param name="MeasuredBytes">Belegung des Installationsordners laut Scan; <c>null</c>, wenn der Ordner nicht im Scan liegt.</param>
public sealed record ProgramSize(InstalledProgram Program, long? MeasuredBytes, DirectoryNode? Folder)
{
    public long? DisplayBytes => MeasuredBytes ?? Program.EstimatedSizeBytes;

    public bool IsMeasured => MeasuredBytes is not null;
}

/// <summary>Liest die installierten Programme. Rein lesend.</summary>
public static class InstalledProgramsProbe
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledProgram> Read()
    {
        var result = new List<InstalledProgram>();
        Read(result, RegistryHive.LocalMachine, RegistryView.Registry64, "Alle Benutzer");
        Read(result, RegistryHive.LocalMachine, RegistryView.Registry32, "Alle Benutzer (32 Bit)");
        Read(result, RegistryHive.CurrentUser, RegistryView.Registry64, "Aktueller Benutzer");

        // Dasselbe Programm kann in mehreren Sichten stehen.
        return result
            .GroupBy(p => (p.Name.ToUpperInvariant(), p.Version, p.InstallLocation?.ToUpperInvariant()))
            .Select(g => g.First())
            .ToArray();
    }

    /// <summary>Ordnet jedem Programm die gemessene Größe aus dem Scan zu und sortiert nach Größe, größte zuerst.</summary>
    public static IReadOnlyList<ProgramSize> Measure(IEnumerable<InstalledProgram> programs, DirectoryNode scanRoot)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(scanRoot);

        return programs
            .Select(p =>
            {
                DirectoryNode? folder = p.InstallLocation is null ? null : scanRoot.FindDescendant(p.InstallLocation);
                return new ProgramSize(p, folder?.TotalSizeOnDisk, folder);
            })
            .OrderByDescending(s => s.DisplayBytes ?? -1)
            .ThenBy(s => s.Program.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static void Read(List<InstalledProgram> into, RegistryHive hive, RegistryView view, string source)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallKey);
        if (uninstall is null)
        {
            return;
        }

        foreach (string name in uninstall.GetSubKeyNames())
        {
            using RegistryKey? key = uninstall.OpenSubKey(name);
            if (key is null || !IsListed(key))
            {
                continue;
            }

            string displayName = (key.GetValue("DisplayName") as string)!.Trim();
            string? location = CleanFolder(key.GetValue("InstallLocation") as string);
            bool inferred = false;
            if (location is null)
            {
                location = InferFromIcon(key.GetValue("DisplayIcon") as string);
                inferred = location is not null;
            }

            long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
            into.Add(new InstalledProgram(
                displayName,
                Nullable(key.GetValue("Publisher") as string),
                Nullable(key.GetValue("DisplayVersion") as string),
                location,
                inferred,
                size,
                ParseDate(key.GetValue("InstallDate") as string),
                source,
                Nullable(key.GetValue("UninstallString") as string)));
        }
    }

    /// <summary>Ohne Namen, als Systemkomponente markiert oder als Update eines anderen Programms eingetragen: nicht aufgeführt.</summary>
    private static bool IsListed(RegistryKey key)
    {
        if (key.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return key.GetValue("SystemComponent") is not 1
            && key.GetValue("ParentKeyName") is null
            && key.GetValue("ParentDisplayName") is null;
    }

    private static string? Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? CleanFolder(string? path)
    {
        string? cleaned = Nullable(path)?.Trim('"');
        if (string.IsNullOrEmpty(cleaned) || !Path.IsPathFullyQualified(cleaned))
        {
            return null;
        }

        try
        {
            cleaned = Path.GetFullPath(cleaned).TrimEnd('\\');
        }
        catch (ArgumentException)
        {
            return null; // ungültige Zeichen im Registry-Wert
        }

        return Directory.Exists(cleaned) && !IsRootOrWindows(cleaned) ? cleaned : null;
    }

    /// <summary>
    /// Ableitung aus dem Symbol-Pfad (z. B. <c>"C:\Programme\X\x.exe",0</c>): der Ordner der Datei, wenn er tiefer als
    /// die Laufwerkswurzel liegt und nicht zu Windows gehört. Eine Näherung, daher in der Anzeige gekennzeichnet.
    /// </summary>
    private static string? InferFromIcon(string? displayIcon)
    {
        string? value = Nullable(displayIcon);
        if (value is null)
        {
            return null;
        }

        int comma = value.LastIndexOf(',');
        if (comma > 1 && int.TryParse(value.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            value = value.Substring(0, comma);
        }

        value = value.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(value))
        {
            return null;
        }

        try
        {
            return CleanFolder(Path.GetDirectoryName(Path.GetFullPath(value)));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Laufwerkswurzel, Windows-Ordner und Sammelordner, in denen viele Programme liegen, gehören keinem einzelnen Programm.</summary>
    private static bool IsRootOrWindows(string folder)
    {
        string? root = Path.GetPathRoot(folder)?.TrimEnd('\\');
        if (string.Equals(folder, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
            && (folder.Length == windows.Length || folder[windows.Length] == '\\'))
        {
            return true;
        }

        string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string[] containers =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            localPrograms,
        };
        return containers.Any(c => c.Length > 0 && string.Equals(folder, c.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime? ParseDate(string? value)
        => value is { Length: 8 } && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date
            : null;
}
