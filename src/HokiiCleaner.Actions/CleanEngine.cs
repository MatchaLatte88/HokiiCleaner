using System.IO.Enumeration;

namespace HokiiCleaner.Actions;

/// <summary>
/// Analysiert und leert die Ziele aus <see cref="CleanTargets"/>. Analyse und Löschen laufen durch denselben Code,
/// damit die Vorschau genau das zeigt, was gelöscht wird. Gelöscht wird endgültig, nur Dateien (und danach leere
/// Unterordner) unterhalb eines geprüften Wurzelordners; Verknüpfungen werden weder betreten noch angefasst.
/// </summary>
public static class CleanEngine
{
    private const int LargestCount = 3;
    private const int SampleLimit = 500;
    private const int ProgressInterval = 250;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private readonly record struct Entry(string Path, string Name, bool IsDirectory, long Length, DateTime NewestUtc, bool IsLink);

    /// <summary>Zählt, was gelöscht würde. Verändert nichts.</summary>
    public static TargetRun Analyze(CleanTarget target, DateTime utcNow, CancellationToken ct)
        => Run(target, utcNow, delete: false, progress: null, ct);

    /// <summary>Löscht die passenden Dateien des Ziels endgültig.</summary>
    public static TargetRun Clean(CleanTarget target, DateTime utcNow, IProgress<CleanProgress>? progress, CancellationToken ct)
        => Run(target, utcNow, delete: true, progress, ct);

    /// <summary>Leert die Ziele nacheinander und misst den freien Platz auf den betroffenen Laufwerken davor und danach.</summary>
    public static CleanResult CleanAll(IReadOnlyList<CleanTarget> targets, IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        DateTime started = DateTime.UtcNow;
        string[] drives = targets
            .SelectMany(t => t.Roots)
            .Select(r => Path.GetPathRoot(Path.GetFullPath(r)) ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        long? before = FreeSpace(drives);
        var runs = new List<TargetRun>();
        foreach (CleanTarget target in targets)
        {
            TargetRun run = Clean(target, DateTime.UtcNow, progress, ct);
            runs.Add(run);
            if (run.Cancelled)
            {
                break;
            }
        }

        return new CleanResult(runs, started, DateTime.UtcNow, before, FreeSpace(drives));
    }

    private static long? FreeSpace(IReadOnlyList<string> drives)
    {
        long sum = 0;
        foreach (string drive in drives)
        {
            try
            {
                sum += new DriveInfo(drive).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        return sum;
    }

    private static TargetRun Run(CleanTarget target, DateTime utcNow, bool delete, IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        var state = new RunState(target);
        DateTime cutoff = utcNow - target.MinAge;
        int missing = 0;

        try
        {
            foreach (string root in target.Roots)
            {
                (RootState rootState, string? reason) = RootGuard.Check(root, target.AllowedRootNames);
                if (rootState == RootState.Missing)
                {
                    missing++;
                    continue;
                }

                if (rootState == RootState.Rejected)
                {
                    state.Problem = state.Problem is null ? reason : state.Problem + " " + reason;
                    continue;
                }

                Walk(root, target, cutoff, delete, progress, state, ct);
            }
        }
        catch (OperationCanceledException)
        {
            state.Cancelled = true;
        }

        return state.ToRun(missing == target.Roots.Count);
    }

    private static void Walk(string root, CleanTarget target, DateTime cutoff, bool delete, IProgress<CleanProgress>? progress, RunState state, CancellationToken ct)
    {
        bool flat = target.FilePatterns is { Count: > 0 };
        var excluded = (target.ExcludedFolders ?? Array.Empty<string>())
            .Select(folder => Path.GetFullPath(Path.Combine(root, folder)).TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        var removableDirectories = new List<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            List<Entry> entries;
            try
            {
                entries = List(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                state.Skip(directory, SkipReason.UnreadableFolder, ex.Message, isFile: false);
                continue;
            }

            foreach (Entry entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.IsLink)
                {
                    state.LinksSkipped++;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    if (flat || excluded.Contains(entry.Path.TrimEnd(Path.DirectorySeparatorChar)))
                    {
                        continue;
                    }

                    pending.Push(entry.Path);
                    if (entry.NewestUtc <= cutoff)
                    {
                        removableDirectories.Add(entry.Path);
                    }

                    continue;
                }

                if (flat && !target.FilePatterns!.Any(p => FileSystemName.MatchesSimpleExpression(p, entry.Name, ignoreCase: true)))
                {
                    continue;
                }

                if (entry.NewestUtc > cutoff || !RootGuard.IsInside(root, entry.Path))
                {
                    continue;
                }

                state.Matched(entry);
                if (!delete)
                {
                    continue;
                }

                (SkipReason Reason, string? Detail)? failure = TryDelete(entry.Path);
                if (failure is { } f)
                {
                    state.Skip(entry.Path, f.Reason, f.Detail);
                }
                else
                {
                    state.Deleted(entry.Length);
                }

                if (state.Processed % ProgressInterval == 0)
                {
                    progress?.Report(new CleanProgress(target.Name, state.Processed, state.ProcessedBytes));
                }
            }
        }

        if (!delete)
        {
            return;
        }

        // Tiefste zuerst. Ein Ordner wird nur entfernt, wenn er beim Auflisten alt genug war und jetzt leer ist.
        for (int i = removableDirectories.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            RemoveEmptyDirectory(removableDirectories[i], root, state);
        }

        progress?.Report(new CleanProgress(target.Name, state.Processed, state.ProcessedBytes));
    }

    private static List<Entry> List(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false, // Fehler sollen sichtbar werden, nicht verschluckt
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        return new FileSystemEnumerable<Entry>(
            directory,
            static (ref FileSystemEntry e) =>
            {
                DateTime newest = Max(e.LastWriteTimeUtc.UtcDateTime, e.CreationTimeUtc.UtcDateTime, e.LastAccessTimeUtc.UtcDateTime);
                return new Entry(e.ToFullPath(), e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, newest, e.Attributes.HasFlag(FileAttributes.ReparsePoint));
            },
            options).ToList();
    }

    private static DateTime Max(DateTime a, DateTime b, DateTime c) => a > b ? (a > c ? a : c) : (b > c ? b : c);

    private static (SkipReason Reason, string? Detail)? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null; // schon weg
        }
        catch (UnauthorizedAccessException)
        {
            return ClearReadOnlyAndRetry(path);
        }
        catch (IOException ex)
        {
            return Classify(ex);
        }
    }

    /// <summary>Schreibgeschützte Dateien lassen sich nicht löschen; das Attribut wird einmal entfernt, dann wird erneut versucht.</summary>
    private static (SkipReason Reason, string? Detail)? ClearReadOnlyAndRetry(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.ReadOnly))
            {
                return (SkipReason.AccessDenied, null);
            }

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return (SkipReason.AccessDenied, null);
        }
        catch (FileNotFoundException)
        {
            return null; // inzwischen weg
        }
        catch (IOException ex)
        {
            return Classify(ex);
        }
    }

    private static (SkipReason Reason, string? Detail) Classify(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is SharingViolation or LockViolation
            ? (SkipReason.InUse, null)
            : (SkipReason.Failed, ex.Message);
    }

    private static void RemoveEmptyDirectory(string path, string root, RunState state)
    {
        if (!RootGuard.IsInside(root, path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: false);
            state.DirectoriesRemoved++;
        }
        catch (DirectoryNotFoundException)
        {
            // schon weg
        }
        catch (IOException ex)
        {
            // "Nicht leer" ist der Normalfall (jüngere Dateien bleiben); alles andere wird gemeldet.
            if (IsEmpty(path))
            {
                state.Skip(path, Classify(ex).Reason, ex.Message, isFile: false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            state.Skip(path, SkipReason.AccessDenied, null, isFile: false);
        }
    }

    private static bool IsEmpty(string path)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // nicht beurteilbar: gilt als "nicht leer", der Ordner bleibt einfach stehen
        }
    }

    private sealed class RunState
    {
        private readonly CleanTarget _target;
        private readonly List<LargeFile> _largest = new();
        private readonly Dictionary<SkipReason, long> _skippedCounts = new();
        private readonly List<SkippedItem> _sample = new();
        private long _matchedFiles;
        private long _matchedBytes;
        private long _deletedFiles;
        private long _deletedBytes;
        private long _skippedFileCount;

        public RunState(CleanTarget target) => _target = target;

        public string? Problem { get; set; }

        public bool Cancelled { get; set; }

        public long LinksSkipped { get; set; }

        public long DirectoriesRemoved { get; set; }

        /// <summary>Dateien, an denen ein Löschversuch lief (gelöscht oder übersprungen).</summary>
        public long Processed => _deletedFiles + _skippedFileCount;

        public long ProcessedBytes => _deletedBytes;

        public void Matched(Entry entry)
        {
            _matchedFiles++;
            _matchedBytes += entry.Length;

            if (_largest.Count < LargestCount || entry.Length > _largest[^1].Length)
            {
                _largest.Add(new LargeFile(entry.Path, entry.Length));
                _largest.Sort(static (a, b) => b.Length.CompareTo(a.Length));
                if (_largest.Count > LargestCount)
                {
                    _largest.RemoveAt(_largest.Count - 1);
                }
            }
        }

        public void Deleted(long length)
        {
            _deletedFiles++;
            _deletedBytes += length;
        }

        public void Skip(string path, SkipReason reason, string? detail, bool isFile = true)
        {
            _skippedCounts[reason] = _skippedCounts.GetValueOrDefault(reason) + 1;
            if (isFile)
            {
                _skippedFileCount++;
            }

            if (_sample.Count < SampleLimit)
            {
                _sample.Add(new SkippedItem(path, reason, detail));
            }
        }

        public TargetRun ToRun(bool missing) => new(
            _target, missing, Problem, _matchedFiles, _matchedBytes, _deletedFiles, _deletedBytes,
            DirectoriesRemoved, LinksSkipped, _largest.ToArray(), new Dictionary<SkipReason, long>(_skippedCounts), _sample.ToArray(), Cancelled);
    }
}
