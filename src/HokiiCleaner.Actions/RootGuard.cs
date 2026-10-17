namespace HokiiCleaner.Actions;

public enum RootState
{
    Ok,

    /// <summary>Der Ordner existiert nicht; das Ziel ist auf diesem Rechner nicht relevant.</summary>
    Missing,

    /// <summary>Der Ordner ist ungeeignet (zu nah an der Laufwerkswurzel, Verknüpfung, unerwarteter Name) und wird nie angefasst.</summary>
    Rejected,
}

/// <summary>Prüft Wurzelordner und Pfade, bevor etwas gelöscht wird. Alles, was nicht eindeutig passt, wird abgelehnt.</summary>
public static class RootGuard
{
    private const int MinDepth = 2;

    public static (RootState State, string? Reason) Check(string root, IReadOnlyList<string>? allowedNames)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            return (RootState.Rejected, $"„{root}“ ist kein vollständiger Pfad.");
        }

        string full = Normalize(root);
        string drive = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        string[] segments = full[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < MinDepth)
        {
            return (RootState.Rejected, $"„{full}“ liegt zu nah an der Laufwerkswurzel.");
        }

        if (allowedNames is { Count: > 0 } && !allowedNames.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
        {
            return (RootState.Rejected, $"„{full}“ heißt nicht {string.Join(" oder ", allowedNames.Select(n => $"„{n}“"))}.");
        }

        var info = new DirectoryInfo(full);
        if (!info.Exists)
        {
            return (RootState.Missing, null);
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return (RootState.Rejected, $"„{full}“ ist eine Verknüpfung und wird nicht betreten.");
        }

        return (RootState.Ok, null);
    }

    /// <summary>Wahr, wenn <paramref name="path"/> unterhalb von <paramref name="root"/> liegt (nicht gleich der Wurzel).</summary>
    public static bool IsInside(string root, string path)
    {
        string normalizedRoot = Normalize(root) + Path.DirectorySeparatorChar;
        string normalizedPath = Normalize(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
