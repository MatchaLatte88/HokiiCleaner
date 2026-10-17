namespace Cleanii.Actions;

/// <summary>Ein Eintrag, den der Benutzer in den Papierkorb legen möchte; Größen stammen aus dem Scan.</summary>
public sealed record RecycleCandidate(string Path, bool IsDirectory, long SizeOnDisk, long FileCount);

public sealed record RecycleRefusal(RecycleCandidate Item, string Reason);

/// <summary>Was verschoben würde und was nicht (mit Begründung). Nichts davon verändert das Dateisystem.</summary>
public sealed record RecyclePlan(
    IReadOnlyList<RecycleCandidate> Accepted,
    IReadOnlyList<RecycleRefusal> Refused,
    IReadOnlyList<string> Notes)
{
    public long AcceptedBytes => Accepted.Sum(c => c.SizeOnDisk);

    public long AcceptedFiles => Accepted.Sum(c => c.FileCount);
}

public enum RecycleStatus
{
    /// <summary>Im Papierkorb.</summary>
    Moved,

    /// <summary>Vor dem Verschieben abgelehnt (Schutzliste oder verändert seit der Planung).</summary>
    Refused,

    /// <summary>Windows hat den Eintrag nicht verschoben (in Benutzung, kein Zugriff, abgebrochen).</summary>
    Failed,

    /// <summary>Der Lauf wurde vorher abgebrochen.</summary>
    NotAttempted,
}

public sealed record RecycleItemResult(RecycleCandidate Item, RecycleStatus Status, string? Detail);

/// <summary>Fortschritt: bearbeitete Einträge und deren Größe.</summary>
public sealed record RecycleProgress(int Done, int Total, string CurrentPath);

public sealed record RecycleResult(
    IReadOnlyList<RecycleItemResult> Items,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    bool Cancelled,
    long? BinItemsBefore,
    long? BinItemsAfter)
{
    public IEnumerable<RecycleItemResult> Moved => Items.Where(i => i.Status == RecycleStatus.Moved);

    public int MovedCount => Moved.Count();

    public long MovedBytes => Moved.Sum(i => i.Item.SizeOnDisk);

    public int ProblemCount => Items.Count(i => i.Status is RecycleStatus.Failed or RecycleStatus.Refused);

    /// <summary>
    /// Wahr, wenn der Papierkorb nach dem Lauf weniger neue Einträge hat als verschoben wurden. Das deutet darauf hin,
    /// dass Windows etwas endgültig gelöscht statt aufgehoben hat (oder der Papierkorb zwischenzeitlich geleert wurde).
    /// </summary>
    public bool BinGrowthLooksShort
        => BinItemsBefore is long before && BinItemsAfter is long after && after - before < MovedCount;
}

/// <summary>Ergebnis eines einzelnen Verschiebens durch Windows.</summary>
public sealed record RecycleOutcome(bool Success, string? Detail)
{
    public static readonly RecycleOutcome Done = new(true, null);
}

/// <summary>Verschiebt genau einen Eintrag in den Papierkorb. Austauschbar, damit Tests nie den echten Papierkorb anfassen.</summary>
public interface IRecycler
{
    RecycleOutcome Recycle(string path, bool isDirectory);
}
