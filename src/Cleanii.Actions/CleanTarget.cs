namespace Cleanii.Actions;

public enum CleanTier
{
    /// <summary>Temporäre Dateien, die jedes Programm jederzeit neu anlegt.</summary>
    Temp,

    /// <summary>Caches von Programmen und Treibern: ohne Datenverlust löschbar, aber mit Folgen (Neu-Download, Neuaufbau).</summary>
    Cache,
}

/// <summary>
/// Ein festgelegter Bereich, den Cleanii aufräumen darf. Nur Inhalte der <see cref="Roots"/> werden berührt, nie die Ordner selbst.
/// Es gibt bewusst keine frei wählbaren Pfade.
/// </summary>
/// <param name="Id">Stabiler Schlüssel.</param>
/// <param name="MinAge">Nur Dateien, die mindestens so lange unberührt sind (jüngster Zeitstempel aus Schreiben, Anlegen, Zugriff).</param>
/// <param name="AllowedRootNames">Wenn gesetzt, muss der Wurzelordner so heißen; fängt falsch gesetzte Umgebungsvariablen ab.</param>
/// <param name="FilePatterns">Wenn gesetzt, werden nur Dateien dieser Muster direkt in der Wurzel berücksichtigt (keine Unterordner).</param>
/// <param name="ExcludedFolders">Ordner relativ zur Wurzel, die samt Inhalt nie betreten, gezählt oder gelöscht werden.</param>
public sealed record CleanTarget(
    string Id,
    string Name,
    string Description,
    string Consequence,
    CleanTier Tier,
    IReadOnlyList<string> Roots,
    TimeSpan MinAge,
    bool DefaultSelected,
    IReadOnlyList<string>? AllowedRootNames = null,
    IReadOnlyList<string>? FilePatterns = null,
    IReadOnlyList<string>? ExcludedFolders = null);

/// <summary>
/// Die Ordner, aus denen alle Ziele abgeleitet werden; austauschbar, damit Tests nie echte Daten berühren. Ziele, die einen
/// der optionalen Ordner brauchen, entfallen, solange er leer ist.
/// </summary>
public sealed record CleanLocations(
    string UserTemp,
    string WindowsTemp,
    string LocalAppData,
    string UserProfile = "",
    string WindowsDirectory = "")
{
    public static CleanLocations FromEnvironment() => new(
        TrimEnd(Path.GetTempPath()),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    private static string TrimEnd(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public static class CleanTargets
{
    private static readonly TimeSpan TempAge = TimeSpan.FromDays(7);
    private static readonly string[] ChromiumCacheFolders = { "Cache", "Code Cache", "GPUCache" };

    public static IReadOnlyList<CleanTarget> Build(CleanLocations locations)
    {
        var targets = new List<CleanTarget>(BuildBase(locations));
        targets.AddRange(BuildBrowsers(locations.LocalAppData));
        targets.AddRange(BuildDeveloper(locations));
        targets.AddRange(BuildWindows(locations));
        return targets;
    }

    private static CleanTarget[] BuildBase(CleanLocations locations)
    {
        string local = locations.LocalAppData;
        return new[]
        {
            new CleanTarget(
                "user-temp", "Temp-Ordner des Benutzers",
                "Zwischendateien von Programmen und Installern in %TEMP%.",
                "Programme legen sie bei Bedarf neu an. Nur Dateien, die seit 7 Tagen unberührt sind.",
                CleanTier.Temp, new[] { locations.UserTemp }, TempAge, true,
                AllowedRootNames: new[] { "Temp", "Tmp" },
                // Arbeitsordner von Claude Code (auch anderer Sitzungen) liegen hier und werden nie angefasst.
                ExcludedFolders: new[] { "claude" }),
            new CleanTarget(
                "windows-temp", "Windows-Temp",
                "Zwischendateien von Windows und Diensten in C:\\Windows\\Temp.",
                "Braucht oft Administratorrechte; nicht löschbare Dateien werden übersprungen und gemeldet. Nur Dateien, die seit 7 Tagen unberührt sind.",
                CleanTier.Temp, new[] { locations.WindowsTemp }, TempAge, true,
                AllowedRootNames: new[] { "Temp" }),
            new CleanTarget(
                "pip-cache", "pip-Cache",
                "Heruntergeladene Python-Pakete von pip.",
                "pip lädt Pakete bei der nächsten Installation erneut herunter.",
                CleanTier.Cache, new[] { Path.Combine(local, "pip", "cache") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "cache" }),
            new CleanTarget(
                "uv-cache", "uv-Cache",
                "Python-Pakete und entpackte Bibliotheken von uv.",
                "uv lädt oder entpackt sie beim nächsten Bedarf neu, das können mehrere GB sein. Bestehende Umgebungen laufen vermutlich weiter, weil uv sie per Hardlink einbindet (nicht geprüft).",
                CleanTier.Cache, new[] { Path.Combine(local, "uv", "cache") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "cache" }),
            new CleanTarget(
                "npm-cache", "npm-Cache",
                "Heruntergeladene Pakete von npm.",
                "npm lädt Pakete bei der nächsten Installation erneut herunter.",
                CleanTier.Cache, new[] { Path.Combine(local, "npm-cache") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "npm-cache" }),
            new CleanTarget(
                "nvidia-shader-cache", "NVIDIA-Shader-Cache",
                "Vorübersetzte Grafik-Shader des NVIDIA-Treibers.",
                "Spiele und Programme bauen die Shader neu auf; der erste Start danach kann ruckeln.",
                CleanTier.Cache, new[] { Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "DXCache", "GLCache" }),
            new CleanTarget(
                "thumbnail-cache", "Explorer-Vorschaubilder",
                "Zwischengespeicherte Vorschaubilder und Symbole des Explorers.",
                "Der Explorer baut sie neu auf. Die Dateien sind meist in Benutzung und werden dann übersprungen.",
                CleanTier.Cache, new[] { Path.Combine(local, "Microsoft", "Windows", "Explorer") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "Explorer" },
                FilePatterns: new[] { "thumbcache_*.db", "iconcache_*.db" }),
        };
    }

    /// <summary>Browser-Caches: nur die Cache-Ordner der Profile; Anmeldungen, Verlauf und Lesezeichen liegen woanders.</summary>
    private static CleanTarget[] BuildBrowsers(string local)
    {
        const string Consequence = "Seiten, Bilder und Skripte werden beim nächsten Besuch neu geladen, das dauert kurz länger. Anmeldungen, Verlauf und Lesezeichen bleiben. Offene Browser halten Dateien fest; die werden übersprungen.";
        return new[]
        {
            Chromium("chrome-cache", "Chrome-Cache", "Zwischengespeicherte Webseiten von Google Chrome.", Path.Combine(local, "Google", "Chrome", "User Data"), Consequence),
            Chromium("edge-cache", "Edge-Cache", "Zwischengespeicherte Webseiten von Microsoft Edge.", Path.Combine(local, "Microsoft", "Edge", "User Data"), Consequence),
            Chromium("brave-cache", "Brave-Cache", "Zwischengespeicherte Webseiten von Brave.", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), Consequence),
            new CleanTarget(
                "firefox-cache", "Firefox-Cache",
                "Zwischengespeicherte Webseiten von Firefox.",
                Consequence,
                CleanTier.Cache,
                ProfileFolders(Path.Combine(local, "Mozilla", "Firefox", "Profiles"), _ => true, new[] { "cache2" }, "Default"),
                TimeSpan.Zero, false,
                AllowedRootNames: new[] { "cache2" }),
        };
    }

    private static CleanTarget Chromium(string id, string name, string description, string userData, string consequence)
        => new(
            id, name, description, consequence,
            CleanTier.Cache,
            ProfileFolders(userData, folder => folder == "Default" || folder.StartsWith("Profile ", StringComparison.Ordinal), ChromiumCacheFolders, "Default"),
            TimeSpan.Zero, false,
            AllowedRootNames: ChromiumCacheFolders);

    /// <summary>
    /// Cache-Ordner aller Profile unter <paramref name="parent"/>. Gibt es kein Profil, bleibt ein Standardpfad
    /// (das Ziel gilt dann als nicht vorhanden und wird nicht angezeigt).
    /// </summary>
    private static string[] ProfileFolders(string parent, Func<string, bool> isProfile, IReadOnlyList<string> cacheFolders, string fallbackProfile)
    {
        var roots = new List<string>();
        try
        {
            if (Directory.Exists(parent))
            {
                foreach (string profile in Directory.EnumerateDirectories(parent))
                {
                    if (isProfile(Path.GetFileName(profile)))
                    {
                        roots.AddRange(cacheFolders.Select(c => Path.Combine(profile, c)));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht lesbar: das Ziel gilt dann als nicht vorhanden, nichts wird angefasst.
            roots.Clear();
        }

        if (roots.Count == 0)
        {
            roots.AddRange(cacheFolders.Select(c => Path.Combine(parent, fallbackProfile, c)));
        }

        return roots.ToArray();
    }

    private static CleanTarget[] BuildDeveloper(CleanLocations locations)
    {
        var targets = new List<CleanTarget>
        {
            new(
                "nuget-http-cache", "NuGet-Zwischenspeicher",
                "Abgerufene Paketlisten und Antworten der NuGet-Server (v3-cache).",
                "NuGet ruft sie beim nächsten Wiederherstellen erneut ab.",
                CleanTier.Cache, new[] { Path.Combine(locations.LocalAppData, "NuGet", "v3-cache") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "v3-cache" }),
            new(
                "yarn-cache", "Yarn-Cache",
                "Heruntergeladene Pakete von Yarn.",
                "Yarn lädt Pakete bei der nächsten Installation erneut herunter.",
                CleanTier.Cache, new[] { Path.Combine(locations.LocalAppData, "Yarn", "Cache") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "Cache" }),
        };

        if (locations.UserProfile.Length > 0)
        {
            targets.Add(new CleanTarget(
                "nuget-packages", "NuGet-Paketordner",
                "Alle bisher geladenen NuGet-Pakete (.nuget\\packages).",
                "Beim nächsten Wiederherstellen (dotnet restore, Visual Studio) lädt NuGet alle gebrauchten Pakete erneut. Ohne Internet lassen sich Projekte danach nicht bauen.",
                CleanTier.Cache, new[] { Path.Combine(locations.UserProfile, ".nuget", "packages") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "packages" }));
            targets.Add(new CleanTarget(
                "gradle-caches", "Gradle-Cache",
                "Heruntergeladene Abhängigkeiten und Zwischenstände von Gradle (.gradle\\caches).",
                "Gradle lädt Abhängigkeiten beim nächsten Build erneut und baut Zwischenstände neu auf. Ohne Internet lassen sich Projekte danach nicht bauen.",
                CleanTier.Cache, new[] { Path.Combine(locations.UserProfile, ".gradle", "caches") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "caches" }));
        }

        return targets.ToArray();
    }

    private static CleanTarget[] BuildWindows(CleanLocations locations)
    {
        var targets = new List<CleanTarget>
        {
            new(
                "crash-dumps", "Absturzabbilder des Benutzers",
                "Speicherabbilder abgestürzter Programme (CrashDumps).",
                "Nur nötig, wenn du Abstürze selbst untersuchen willst. Sonst folgenlos.",
                CleanTier.Cache, new[] { Path.Combine(locations.LocalAppData, "CrashDumps") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "CrashDumps" }),
            new(
                "wer-user", "Windows-Fehlerberichte",
                "Gesammelte Berichte über Programmabstürze (WER) des Benutzers.",
                "Folgenlos; Windows legt neue Berichte bei Bedarf an. Gesendet werden sie dadurch nicht.",
                CleanTier.Cache, new[] { Path.Combine(locations.LocalAppData, "Microsoft", "Windows", "WER") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "WER" }),
        };

        if (locations.WindowsDirectory.Length > 0)
        {
            string windows = locations.WindowsDirectory;
            targets.Add(new CleanTarget(
                "windows-update-download", "Windows-Update-Downloads",
                "Bereits heruntergeladene Update-Dateien (SoftwareDistribution\\Download).",
                "Windows Update lädt benötigte Updates erneut herunter. Braucht Administratorrechte; nicht löschbare Dateien werden übersprungen und gemeldet. Nur Dateien, die seit 7 Tagen unberührt sind.",
                CleanTier.Cache, new[] { Path.Combine(windows, "SoftwareDistribution", "Download") }, TempAge, false,
                AllowedRootNames: new[] { "Download" }));
            targets.Add(new CleanTarget(
                "delivery-optimization", "Übermittlungsoptimierung",
                "Zwischengespeicherte Update-Teile, die Windows an andere Rechner weitergeben kann.",
                "Windows lädt sie bei Bedarf neu. Braucht Administratorrechte; nicht löschbare Dateien werden übersprungen und gemeldet.",
                CleanTier.Cache,
                new[] { Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache") },
                TimeSpan.Zero, false,
                AllowedRootNames: new[] { "Cache" }));
            targets.Add(new CleanTarget(
                "minidump", "Absturzabbilder von Windows",
                "Kleine Speicherabbilder nach Systemabstürzen (Minidump).",
                "Nur nötig, wenn du Systemabstürze selbst untersuchen willst. Braucht Administratorrechte.",
                CleanTier.Cache, new[] { Path.Combine(windows, "Minidump") }, TimeSpan.Zero, false,
                AllowedRootNames: new[] { "Minidump" }));
        }

        return targets.ToArray();
    }
}
