namespace HokiiCleaner.Actions;

public enum SkipReason
{
    /// <summary>Eine andere Anwendung hat die Datei geöffnet.</summary>
    InUse,

    /// <summary>Windows verweigert den Zugriff (oft: Administratorrechte nötig).</summary>
    AccessDenied,

    /// <summary>Ein Ordner ließ sich nicht auflisten; sein Inhalt wurde nicht berücksichtigt.</summary>
    UnreadableFolder,

    /// <summary>Anderer Fehler; Details stehen im Eintrag.</summary>
    Failed,
}

public sealed record LargeFile(string Path, long Length);

public sealed record SkippedItem(string Path, SkipReason Reason, string? Detail);

/// <summary>Ergebnis eines Durchlaufs über ein Ziel; bei der Analyse sind die gelöschten Werte 0.</summary>
public sealed record TargetRun(
    CleanTarget Target,
    bool Missing,
    string? Problem,
    long MatchedFiles,
    long MatchedBytes,
    long DeletedFiles,
    long DeletedBytes,
    long DirectoriesRemoved,
    long LinksSkipped,
    IReadOnlyList<LargeFile> Largest,
    IReadOnlyDictionary<SkipReason, long> SkippedCounts,
    IReadOnlyList<SkippedItem> SkippedSample,
    bool Cancelled)
{
    public long SkippedTotal => SkippedCounts.Values.Sum();
}

/// <summary>Fortschritt während des Löschens.</summary>
public sealed record CleanProgress(string TargetName, long FilesProcessed, long BytesProcessed);

public sealed record CleanResult(
    IReadOnlyList<TargetRun> Runs,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    long? FreeBytesBefore,
    long? FreeBytesAfter)
{
    public long DeletedFiles => Runs.Sum(r => r.DeletedFiles);

    public long DeletedBytes => Runs.Sum(r => r.DeletedBytes);

    public bool Cancelled => Runs.Any(r => r.Cancelled);

    /// <summary>Zuwachs an freiem Platz, gemessen auf den betroffenen Laufwerken; <c>null</c>, wenn nicht messbar.</summary>
    public long? FreedBytesMeasured => FreeBytesBefore is long before && FreeBytesAfter is long after ? after - before : null;
}
