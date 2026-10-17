using System.Security.Cryptography;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Core.Duplicates;

public sealed record DuplicateOptions
{
    /// <summary>Kleinere Dateien werden nicht verglichen (logische Größe).</summary>
    public long MinLengthBytes { get; init; } = 1024 * 1024;

    public bool IncludeHidden { get; init; }

    public bool IncludeSystem { get; init; }

    /// <summary>Gleichzeitig gelesene Dateien. Wenige, damit eine Festplatte nicht ins Springen gerät.</summary>
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

/// <summary>Eine Datei einer Duplikatgruppe.</summary>
public sealed record DuplicateFile(DirectoryNode Folder, FileEntry File)
{
    public string FullPath => Path.Combine(Folder.FullPath, File.Name);
}

/// <param name="Length">Logische Größe jeder Datei der Gruppe.</param>
/// <param name="SizeOnDisk">Belegung einer einzelnen Datei auf dem Datenträger.</param>
public sealed record DuplicateGroup(long Length, long SizeOnDisk, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Platz, der frei würde, wenn nur eine Kopie bliebe.</summary>
    public long ReclaimableBytes => SizeOnDisk * (Files.Count - 1);
}

/// <summary>Eine Datei, die beim Vergleich nicht gelesen werden konnte.</summary>
public sealed record DuplicateIssue(string Path, string Message);

/// <param name="CandidateFiles">Dateien, die nach Größe und Filtern überhaupt verglichen wurden.</param>
/// <param name="SkippedCloudFiles">Dateien, die nur online liegen oder Verknüpfungen sind und nicht gelesen wurden.</param>
public sealed record DuplicateReport(
    IReadOnlyList<DuplicateGroup> Groups,
    long CandidateFiles,
    long SkippedCloudFiles,
    long HashedBytes,
    IReadOnlyList<DuplicateIssue> Issues,
    TimeSpan Elapsed)
{
    public long ReclaimableBytes => Groups.Sum(g => g.ReclaimableBytes);
}

public enum DuplicateStage
{
    Collecting,
    PartialHash,
    FullHash,
}

public sealed record DuplicateProgress(DuplicateStage Stage, long FilesDone, long FilesTotal, long BytesDone, long BytesTotal);

/// <summary>
/// Findet Dateien mit identischem Inhalt. Rein lesend: Die Dateien werden nur gelesen, nie verändert oder gelöscht.
/// Vorgehen: gleiche Größe, dann gleicher Anfang und Ende, dann gleicher SHA-256 über die ganze Datei.
/// Hardlinks (Kopien, die denselben Platz belegen) zählen nicht als Duplikat.
/// </summary>
public static class DuplicateFinder
{
    private const int PartialBytes = 64 * 1024;
    private const int BufferBytes = 1024 * 1024;

    private sealed class Candidate(DirectoryNode folder, FileEntry file)
    {
        public DirectoryNode Folder { get; } = folder;

        public FileEntry File { get; } = file;

        public string FullPath => Path.Combine(Folder.FullPath, File.Name);

        /// <summary>Kennung der Datei auf dem Datenträger (Laufwerk und Index); Hardlinks teilen sie. Erst nach dem vollen Lesen bekannt.</summary>
        public string? FileId { get; set; }
    }

    public static async Task<DuplicateReport> FindAsync(
        DirectoryNode scope,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        options ??= new DuplicateOptions();
        if (options.MinLengthBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinLengthBytes muss mindestens 1 sein.");
        }

        if (options.Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Parallelism muss mindestens 1 sein.");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var issues = new System.Collections.Concurrent.ConcurrentQueue<DuplicateIssue>();

        progress?.Report(new DuplicateProgress(DuplicateStage.Collecting, 0, 0, 0, 0));
        (List<Candidate> candidates, long skippedCloud) = await Task.Run(() => Collect(scope, options, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        List<List<Candidate>> sameSize = candidates
            .GroupBy(c => c.File.Length)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();

        // Stufe 2: Anfang und Ende. Verwirft die meisten gleich großen, aber verschiedenen Dateien billig.
        long partialFiles = sameSize.Sum(g => g.Count);
        long partialDone = 0;
        var partialGroups = new List<List<Candidate>>();
        foreach (List<Candidate> group in sameSize)
        {
            var byKey = new Dictionary<string, List<Candidate>>();
            await ForEachLimitedAsync(group, options.Parallelism, cancellationToken, candidate =>
            {
                string? key = TryHash(candidate, partial: true, issues, cancellationToken, out _);
                if (key is not null)
                {
                    lock (byKey)
                    {
                        if (!byKey.TryGetValue(key, out List<Candidate>? list))
                        {
                            byKey[key] = list = new List<Candidate>();
                        }

                        list.Add(candidate);
                    }
                }

                long done = Interlocked.Increment(ref partialDone);
                progress?.Report(new DuplicateProgress(DuplicateStage.PartialHash, done, partialFiles, 0, 0));
            }).ConfigureAwait(false);

            partialGroups.AddRange(byKey.Values.Where(l => l.Count > 1));
        }

        // Stufe 3: ganzer Inhalt.
        long fullFiles = partialGroups.Sum(g => g.Count);
        long fullBytesTotal = partialGroups.Sum(g => g.Sum(c => c.File.Length));
        long filesDone = 0;
        long bytesDone = 0;
        var groups = new List<DuplicateGroup>();
        foreach (List<Candidate> group in partialGroups)
        {
            var byHash = new Dictionary<string, List<Candidate>>();
            await ForEachLimitedAsync(group, options.Parallelism, cancellationToken, candidate =>
            {
                string? key = TryHash(candidate, partial: false, issues, cancellationToken, out long read);
                if (key is not null)
                {
                    lock (byHash)
                    {
                        if (!byHash.TryGetValue(key, out List<Candidate>? list))
                        {
                            byHash[key] = list = new List<Candidate>();
                        }

                        list.Add(candidate);
                    }
                }

                // Die Zähler gehören auch ohne Fortschrittsanzeige gezählt, daher nicht in den Argumenten von "?.Report".
                long files = Interlocked.Increment(ref filesDone);
                long bytes = Interlocked.Add(ref bytesDone, read);
                progress?.Report(new DuplicateProgress(DuplicateStage.FullHash, files, fullFiles, bytes, fullBytesTotal));
            }).ConfigureAwait(false);

            foreach (List<Candidate> same in byHash.Values.Where(l => l.Count > 1))
            {
                // Hardlinks auf dieselbe Datei belegen den Platz nur einmal und sind keine Kopien.
                List<Candidate> distinct = same
                    .OrderBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase)
                    .GroupBy(c => c.FileId ?? c.FullPath)
                    .Select(g => g.First())
                    .ToList();
                if (distinct.Count < 2)
                {
                    continue;
                }

                FileEntry first = distinct[0].File;
                groups.Add(new DuplicateGroup(
                    first.Length,
                    first.SizeOnDisk,
                    distinct.Select(c => new DuplicateFile(c.Folder, c.File)).ToArray()));
            }
        }

        stopwatch.Stop();
        return new DuplicateReport(
            groups.OrderByDescending(g => g.ReclaimableBytes).ThenBy(g => g.Files[0].FullPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            candidates.Count,
            skippedCloud,
            Interlocked.Read(ref bytesDone),
            issues.ToArray(),
            stopwatch.Elapsed);
    }

    private static (List<Candidate> Candidates, long SkippedCloud) Collect(
        DirectoryNode scope, DuplicateOptions options, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        long skippedCloud = 0;
        var stack = new Stack<DirectoryNode>();
        stack.Push(scope);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryNode node = stack.Pop();
            foreach (FileEntry file in node.Files)
            {
                if (file.IsHardLinkDuplicate || file.Length < options.MinLengthBytes)
                {
                    continue;
                }

                if (!options.IncludeHidden && file.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }

                if (!options.IncludeSystem && file.Attributes.HasFlag(FileAttributes.System))
                {
                    continue;
                }

                // Belegung 0 bei vorhandener Länge: Cloud-Platzhalter oder Verknüpfung. Lesen würde die Datei herunterladen.
                if (file.SizeOnDisk == 0)
                {
                    skippedCloud++;
                    continue;
                }

                candidates.Add(new Candidate(node, file));
            }

            foreach (DirectoryNode child in node.Children)
            {
                stack.Push(child);
            }
        }

        return (candidates, skippedCloud);
    }

    /// <summary>Hash über Anfang und Ende (<paramref name="partial"/>) oder die ganze Datei; <c>null</c> mit Eintrag in <paramref name="issues"/>, wenn nicht lesbar.</summary>
    private static string? TryHash(
        Candidate candidate,
        bool partial,
        System.Collections.Concurrent.ConcurrentQueue<DuplicateIssue> issues,
        CancellationToken cancellationToken,
        out long bytesRead)
    {
        bytesRead = 0;
        string path = candidate.FullPath;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

            if (stream.Length != candidate.File.Length)
            {
                issues.Enqueue(new DuplicateIssue(path, "Die Datei hat sich seit dem Scan geändert und wurde nicht verglichen."));
                return null;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (partial)
            {
                var buffer = new byte[PartialBytes];
                int first = ReadFully(stream, buffer);
                hash.AppendData(buffer, 0, first);
                if (stream.Length > PartialBytes)
                {
                    stream.Seek(-Math.Min(PartialBytes, stream.Length - PartialBytes), SeekOrigin.End);
                    int last = ReadFully(stream, buffer);
                    hash.AppendData(buffer, 0, last);
                }

                return Convert.ToHexString(hash.GetHashAndReset());
            }

            var big = new byte[BufferBytes];
            int read;
            while ((read = stream.Read(big, 0, big.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(big, 0, read);
                bytesRead += read;
            }

            if (NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out NativeMethods.ByHandleFileInformation info))
            {
                candidate.FileId = $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            issues.Enqueue(new DuplicateIssue(path, ex.Message));
            return null;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static Task ForEachLimitedAsync<T>(IEnumerable<T> items, int parallelism, CancellationToken cancellationToken, Action<T> action)
        => Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            (item, token) =>
            {
                token.ThrowIfCancellationRequested();
                action(item);
                return ValueTask.CompletedTask;
            });
}
