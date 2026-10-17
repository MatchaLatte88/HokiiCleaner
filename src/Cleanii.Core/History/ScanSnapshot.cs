using System.IO.Compression;
using System.Text.Json;
using Cleanii.Core.Scanning;

namespace Cleanii.Core.History;

/// <summary>Ein Ordner im gespeicherten Stand: Pfad und Belegung. Ordner unter der Mindestgröße werden nicht aufgenommen.</summary>
public sealed record SnapshotFolder(string Path, long SizeOnDisk, long Files);

/// <summary>
/// Ein gespeicherter Stand eines Scans (nur Ordner ab einer Mindestgröße, keine einzelnen Dateien), um später zu sehen,
/// was gewachsen ist. Enthält Pfade und Größen und bleibt auf diesem Rechner.
/// </summary>
public sealed record ScanSnapshot(
    int Version,
    string RootPath,
    DateTime TakenUtc,
    long TotalSizeOnDisk,
    long TotalFiles,
    long MinFolderBytes,
    IReadOnlyList<SnapshotFolder> Folders)
{
    public const int CurrentVersion = 1;
}

/// <summary>Kopfdaten eines gespeicherten Stands, ohne die Ordnerliste zu laden.</summary>
public sealed record SnapshotInfo(string FilePath, string RootPath, DateTime TakenUtc, long TotalSizeOnDisk, long FileBytes);

public static class SnapshotBuilder
{
    /// <summary>Ordner ab dieser Belegung werden gespeichert; kleinere Änderungen bleiben im Vergleich unsichtbar.</summary>
    public const long DefaultMinFolderBytes = 50L * 1024 * 1024;

    private const int MaxFolders = 20_000;

    public static ScanSnapshot Build(DirectoryNode root, DateTime takenUtc, long minFolderBytes = DefaultMinFolderBytes)
    {
        ArgumentNullException.ThrowIfNull(root);

        var folders = new List<SnapshotFolder>();
        var pending = new Stack<DirectoryNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DirectoryNode node = pending.Pop();
            if (node == root || node.TotalSizeOnDisk >= minFolderBytes)
            {
                folders.Add(new SnapshotFolder(node.FullPath, node.TotalSizeOnDisk, node.TotalFileCount));
                foreach (DirectoryNode child in node.Children)
                {
                    pending.Push(child);
                }
            }
        }

        // Bei sehr vielen Ordnern bleiben die größten.
        IReadOnlyList<SnapshotFolder> kept = folders.Count > MaxFolders
            ? folders.OrderByDescending(f => f.SizeOnDisk).Take(MaxFolders).ToArray()
            : folders;
        return new ScanSnapshot(
            ScanSnapshot.CurrentVersion, root.FullPath, takenUtc, root.TotalSizeOnDisk, root.TotalFileCount, minFolderBytes, kept);
    }
}

/// <summary>Speichert gespeicherte Stände als komprimierte JSON-Dateien in einem Ordner. Schreibt nur auf ausdrücklichen Aufruf.</summary>
public static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Save(string directory, ScanSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(directory);
        string label = string.Concat(snapshot.RootPath.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        if (label.Length == 0)
        {
            label = "scan";
        }

        string path = Path.Combine(directory, $"{label}-{snapshot.TakenUtc.ToLocalTime():yyyyMMdd-HHmmss}.json.gz");
        using FileStream file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gzip, snapshot, Options);
        return path;
    }

    /// <summary>Liest einen Stand. Wirft bei beschädigten oder unbekannten Dateien (<see cref="InvalidDataException"/>).</summary>
    public static ScanSnapshot Load(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            ScanSnapshot snapshot = JsonSerializer.Deserialize<ScanSnapshot>(gzip, Options)
                ?? throw new InvalidDataException("Die Datei enthält keinen Stand.");
            if (snapshot.Version != ScanSnapshot.CurrentVersion)
            {
                throw new InvalidDataException($"Unbekannte Version {snapshot.Version} (erwartet {ScanSnapshot.CurrentVersion}).");
            }

            return snapshot;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Die Datei ist beschädigt: {ex.Message}", ex);
        }
    }

    /// <summary>Alle gespeicherten Stände, neueste zuerst. Beschädigte Dateien erscheinen nicht, sondern in <paramref name="problems"/>.</summary>
    public static IReadOnlyList<SnapshotInfo> List(string directory, out IReadOnlyList<string> problems)
    {
        var infos = new List<SnapshotInfo>();
        var issues = new List<string>();
        problems = issues;
        if (!Directory.Exists(directory))
        {
            return infos;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json.gz"))
        {
            try
            {
                ScanSnapshot snapshot = Load(path);
                infos.Add(new SnapshotInfo(path, snapshot.RootPath, snapshot.TakenUtc, snapshot.TotalSizeOnDisk, new FileInfo(path).Length));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                issues.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return infos.OrderByDescending(i => i.TakenUtc).ToArray();
    }
}

/// <summary>Eine Ordnerveränderung zwischen zwei Ständen. Fehlt ein Ordner in einem Stand, war er dort unter der Mindestgröße (oder nicht vorhanden).</summary>
public sealed record FolderChange(string Path, long OldBytes, long NewBytes)
{
    public long Delta => NewBytes - OldBytes;
}

public sealed record SnapshotComparison(ScanSnapshot Old, ScanSnapshot Current, long TotalDelta, IReadOnlyList<FolderChange> Changes);

public static class SnapshotComparer
{
    /// <summary>
    /// Vergleicht zwei Stände desselben Ordners. Ein Ordner, dessen Änderung fast vollständig aus einem Unterordner stammt
    /// (mindestens 80 %, gleiche Richtung), wird nicht zusätzlich aufgeführt: Der Unterordner zeigt die Ursache genauer.
    /// </summary>
    public static SnapshotComparison Compare(ScanSnapshot old, ScanSnapshot current, long minDeltaBytes = 20L * 1024 * 1024, int maxChanges = 200)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(current);

        var oldBySize = old.Folders.ToDictionary(f => Normalize(f.Path), f => f.SizeOnDisk, StringComparer.OrdinalIgnoreCase);
        var newBySize = current.Folders.ToDictionary(f => Normalize(f.Path), f => f.SizeOnDisk, StringComparer.OrdinalIgnoreCase);
        string rootKey = Normalize(current.RootPath);

        var all = oldBySize.Keys.Union(newBySize.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(p => !string.Equals(p, rootKey, StringComparison.OrdinalIgnoreCase))
            .Select(p => new FolderChange(p, oldBySize.GetValueOrDefault(p), newBySize.GetValueOrDefault(p)))
            .Where(c => Math.Abs(c.Delta) >= minDeltaBytes)
            .ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);

        // Größte Änderung unterhalb jedes Ordners gleicher Richtung, damit Eltern, die nur die Summe zeigen, entfallen.
        var strongestBelow = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderChange change in all.Values)
        {
            for (string? parent = System.IO.Path.GetDirectoryName(change.Path); !string.IsNullOrEmpty(parent); parent = System.IO.Path.GetDirectoryName(parent))
            {
                if (all.TryGetValue(parent, out FolderChange? ancestor) && Math.Sign(ancestor.Delta) == Math.Sign(change.Delta)
                    && Math.Abs(change.Delta) > strongestBelow.GetValueOrDefault(parent))
                {
                    strongestBelow[parent] = Math.Abs(change.Delta);
                }
            }
        }

        FolderChange[] changes = all.Values
            .Where(c => strongestBelow.GetValueOrDefault(c.Path) < 0.8 * Math.Abs(c.Delta))
            .OrderByDescending(c => Math.Abs(c.Delta))
            .Take(maxChanges)
            .ToArray();
        return new SnapshotComparison(old, current, current.TotalSizeOnDisk - old.TotalSizeOnDisk, changes);
    }

    private static string Normalize(string path) => path.TrimEnd('\\');
}
