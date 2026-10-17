namespace Cleanii.Core.Scanning;

public sealed record ScanOptions
{
    /// <summary>Anzahl paralleler Lese-Worker. Dateisystem-Zugriffe sind I/O-gebunden, daher mehr als Kerne.</summary>
    public int Parallelism { get; init; } = Math.Min(16, Environment.ProcessorCount * 2);

    /// <summary>
    /// Ordner, in denen Dateien auf Hardlinks geprüft werden (pro Datei ein Handle-Zugriff, daher gezielt).
    /// <c>null</c> = <c>&lt;Laufwerk&gt;\Windows\WinSxS</c>; leere Liste = keine Prüfung.
    /// </summary>
    public IReadOnlyList<string>? HardLinkProbeDirectories { get; init; }

    /// <summary>Mindestabstand zwischen Fortschrittsmeldungen.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}
