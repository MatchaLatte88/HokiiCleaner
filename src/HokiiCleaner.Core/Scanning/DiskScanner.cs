namespace HokiiCleaner.Core.Scanning;

/// <summary>Liest einen Datenträger oder Ordner rein lesend und liefert einen Größenbaum.</summary>
public static class DiskScanner
{
    /// <exception cref="DirectoryNotFoundException">Die Scan-Wurzel existiert nicht.</exception>
    /// <exception cref="OperationCanceledException">Der Scan wurde abgebrochen; ein Teilergebnis wird nicht geliefert.</exception>
    public static Task<ScanResult> ScanAsync(
        string rootPath,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Scan-Wurzel nicht gefunden: {fullRoot}");
        }

        var session = new ScanSession(fullRoot, options ?? new ScanOptions(), progress);
        return session.RunAsync(cancellationToken);
    }
}
