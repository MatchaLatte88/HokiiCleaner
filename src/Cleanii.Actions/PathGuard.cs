namespace Cleanii.Actions;

/// <summary>Ergebnis der Schutzprüfung für einen Pfad.</summary>
public sealed record GuardVerdict(bool Allowed, string? Reason)
{
    public static readonly GuardVerdict Ok = new(true, null);

    public static GuardVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// Orte, die nie angefasst werden. <paramref name="Trees"/> gelten samt allem darunter, <paramref name="Exact"/> nur für
/// den Ordner selbst (sein Inhalt bleibt wählbar), <paramref name="DriveRootNames"/> für Einträge direkt auf einem Laufwerk.
/// </summary>
public sealed record ProtectedLocations(
    IReadOnlyList<string> Trees,
    IReadOnlyList<string> Exact,
    IReadOnlyList<string> DriveRootNames)
{
    private static readonly string[] SystemNamesOnDriveRoot =
    {
        "$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI", "bootmgr", "BOOTNXT",
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp", "Config.Msi", "$WinREAgent",
        "Documents and Settings", "PerfLogs", "$SysReset", "$GetCurrent",
    };

    public static ProtectedLocations FromEnvironment()
    {
        string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

        string profile = Folder(Environment.SpecialFolder.UserProfile);
        string roaming = Folder(Environment.SpecialFolder.ApplicationData);
        string local = Folder(Environment.SpecialFolder.LocalApplicationData);
        string programData = Folder(Environment.SpecialFolder.CommonApplicationData);

        var trees = new List<string>
        {
            Folder(Environment.SpecialFolder.Windows),
            Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86),
            Folder(Environment.SpecialFolder.CommonProgramFiles),
            Folder(Environment.SpecialFolder.CommonProgramFilesX86),
            Path.Combine(programData, "Microsoft"),
            Path.Combine(roaming, "Microsoft", "Protect"),
            Path.Combine(roaming, "Microsoft", "Credentials"),
            Path.Combine(roaming, "Microsoft", "Crypto"),
            Path.Combine(local, "Microsoft", "Credentials"),
            Path.Combine(local, "Cleanii"),
            AppContext.BaseDirectory,
        };

        var exact = new List<string>
        {
            profile,
            Path.GetDirectoryName(profile) ?? profile,
            programData,
            Path.Combine(profile, "AppData"),
            roaming,
            local,
            Path.Combine(local, "Programs"),
            Path.Combine(profile, "AppData", "LocalLow"),
            Folder(Environment.SpecialFolder.Desktop),
            Folder(Environment.SpecialFolder.MyDocuments),
            Folder(Environment.SpecialFolder.MyPictures),
            Folder(Environment.SpecialFolder.MyMusic),
            Folder(Environment.SpecialFolder.MyVideos),
            Path.Combine(profile, "Downloads"),
        };

        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } oneDrive)
            {
                exact.Add(oneDrive);
            }
        }

        return new ProtectedLocations(Clean(trees), Clean(exact), SystemNamesOnDriveRoot);
    }

    /// <summary>Fügt eigene geschützte Ordner (samt Inhalt) hinzu, z. B. aus den Einstellungen.</summary>
    public ProtectedLocations WithTrees(IEnumerable<string> extra)
        => this with { Trees = Trees.Concat(Clean(extra)).ToArray() };

    private static string[] Clean(IEnumerable<string> paths)
        => paths
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Entscheidet, ob ein Pfad vom Benutzer in den Papierkorb gelegt werden darf. Alles, was zu Windows, installierten
/// Programmen oder dem Benutzerprofil selbst gehört, wird abgelehnt, ebenso Ordner, die solche Orte enthalten.
/// </summary>
public sealed class PathGuard
{
    private readonly ProtectedLocations _locations;

    public PathGuard(ProtectedLocations locations)
    {
        _locations = locations;
    }

    public static PathGuard ForCurrentUser(IEnumerable<string>? extraProtected = null)
    {
        ProtectedLocations locations = ProtectedLocations.FromEnvironment();
        return new PathGuard(extraProtected is null ? locations : locations.WithTrees(extraProtected));
    }

    /// <param name="probeFileSystem">Prüft zusätzlich am Dateisystem, ob der Pfad existiert und keine Verknüpfung ist.</param>
    public GuardVerdict Check(string path, bool probeFileSystem)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return GuardVerdict.Deny("Kein vollständiger Pfad.");
        }

        string full = Normalize(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return GuardVerdict.Deny("Netzwerkpfade haben keinen Papierkorb.");
        }

        string drive = root.TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length <= drive.Length)
        {
            return GuardVerdict.Deny("Ein ganzes Laufwerk lässt sich nicht verschieben.");
        }

        string[] segments = full[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1 && _locations.DriveRootNames.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return GuardVerdict.Deny($"„{segments[0]}“ gehört zu Windows und wird nie angefasst.");
        }

        foreach (string tree in _locations.Trees)
        {
            if (string.Equals(full, tree, StringComparison.OrdinalIgnoreCase) || IsInside(tree, full))
            {
                return GuardVerdict.Deny($"Liegt in „{tree}“, das zu Windows, installierten Programmen oder Cleanii gehört.");
            }

            if (IsInside(full, tree))
            {
                return GuardVerdict.Deny($"Enthält „{tree}“, das zu Windows, installierten Programmen oder Cleanii gehört.");
            }
        }

        foreach (string exact in _locations.Exact)
        {
            if (string.Equals(full, exact, StringComparison.OrdinalIgnoreCase))
            {
                return GuardVerdict.Deny("Ein Systemordner des Benutzerprofils; sein Inhalt lässt sich einzeln wählen.");
            }

            if (IsInside(full, exact))
            {
                return GuardVerdict.Deny($"Enthält „{exact}“, einen Systemordner des Benutzerprofils.");
            }
        }

        return probeFileSystem ? ProbeFileSystem(full) : GuardVerdict.Ok;
    }

    /// <summary>Name des Synchronisierungsdienstes, wenn der Pfad in einem OneDrive-Ordner liegt (Löschen wird dann mit der Cloud abgeglichen); sonst <c>null</c>.</summary>
    public static string? SyncProvider(string path)
    {
        string full = Normalize(path);
        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } folder
                && IsInside(Normalize(folder), full))
            {
                return "OneDrive";
            }
        }

        return null;
    }

    private static GuardVerdict ProbeFileSystem(string full)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(full);
            return attributes.HasFlag(FileAttributes.ReparsePoint)
                ? GuardVerdict.Deny("Eine Verknüpfung (Symlink oder Junction); ihr Ziel würde nicht mit verschoben.")
                : GuardVerdict.Ok;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return GuardVerdict.Deny("Existiert nicht mehr.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return GuardVerdict.Deny($"Nicht lesbar: {ex.Message}");
        }
    }

    private static bool IsInside(string folder, string path)
        => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
