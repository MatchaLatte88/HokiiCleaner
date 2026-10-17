namespace HokiiCleaner.Core.Scanning;

/// <summary>Ein Problem während des Scans (z. B. Zugriff verweigert). Nichts davon wird stillschweigend verworfen.</summary>
public sealed record ScanIssue(string Path, string Message);

public sealed record ScanProgress(
    long Files,
    long Directories,
    long LogicalBytes,
    int Issues,
    string CurrentDirectory,
    TimeSpan Elapsed);

public sealed record ScanResult(
    DirectoryNode Root,
    IReadOnlyList<ScanIssue> Issues,
    long ClusterSize,
    TimeSpan Elapsed,
    long HardLinkDuplicateFiles,
    long HardLinkDuplicateBytes);
