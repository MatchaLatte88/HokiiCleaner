using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace HokiiCleaner.Core.Scanning;

/// <summary>
/// Ein einzelner Scan-Lauf. Phase 1: parallel lesen (jeder Ordner gehört genau einem Worker).
/// Phase 2: einthreadig in fester Reihenfolge Hardlinks entduplizieren und Summen bilden,
/// damit das Ergebnis nicht vom Thread-Timing abhängt.
/// </summary>
internal sealed class ScanSession
{
    // FileAttributes kennt diese Cloud-Flags nicht: FILE_ATTRIBUTE_RECALL_ON_OPEN / _RECALL_ON_DATA_ACCESS.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudPlaceholder = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private readonly string _rootPath;
    private readonly ScanOptions _options;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly ConcurrentQueue<ScanIssue> _issues = new();
    private readonly string[] _probeDirectories;
    private readonly long _clusterSize;
    private readonly Stopwatch _stopwatch = new();

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,   // Fehler sollen sichtbar werden, nicht verschluckt
        AttributesToSkip = 0,          // Standard würde Hidden und System überspringen
        ReturnSpecialDirectories = false,
    };

    private Channel<DirectoryNode> _queue = null!;
    private int _pending;
    private long _files;
    private long _directories;
    private long _logicalBytes;
    private volatile string _currentDirectory = string.Empty;

    private readonly record struct RawEntry(string Name, bool IsDirectory, long Length, FileAttributes Attributes, DateTime LastWriteUtc);

    public ScanSession(string rootPath, ScanOptions options, IProgress<ScanProgress>? progress)
    {
        if (options.Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Parallelism muss mindestens 1 sein.");
        }

        _rootPath = rootPath;
        _options = options;
        _progress = progress;
        _clusterSize = GetClusterSize(rootPath);
        _probeDirectories = (options.HardLinkProbeDirectories
                ?? new[] { Path.Combine(Path.GetPathRoot(rootPath)!, "Windows", "WinSxS") })
            .Select(p => Path.GetFullPath(p).TrimEnd('\\'))
            .ToArray();
    }

    public async Task<ScanResult> RunAsync(CancellationToken cancellationToken)
    {
        _stopwatch.Start();

        var root = new DirectoryNode(_rootPath, null) { ProbeHardLinks = IsUnderProbeDirectory(_rootPath) };
        _queue = Channel.CreateUnbounded<DirectoryNode>();
        _pending = 1;
        _queue.Writer.TryWrite(root);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reporterStop = new CancellationTokenSource();
        ExceptionDispatchInfo? failure = null;

        var workers = Enumerable.Range(0, _options.Parallelism)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await WorkAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Abbruch wird nach dem Warten gemeldet; ein Fehler hat Vorrang.
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                    linked.Cancel();
                }
            }))
            .ToArray();

        Task reporter = _progress is null ? Task.CompletedTask : ReportLoopAsync(reporterStop.Token);

        await Task.WhenAll(workers).ConfigureAwait(false);
        reporterStop.Cancel();
        await reporter.ConfigureAwait(false);

        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();

        var (duplicateFiles, duplicateBytes) = Aggregate(root);
        _stopwatch.Stop();
        ReportProgress();

        return new ScanResult(
            root,
            _issues.ToArray(),
            _clusterSize,
            _stopwatch.Elapsed,
            duplicateFiles,
            duplicateBytes);
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        await foreach (DirectoryNode node in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                ProcessDirectory(node);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _queue.Writer.TryComplete();
                }
            }
        }
    }

    private void ProcessDirectory(DirectoryNode node)
    {
        string path = node.FullPath;
        _currentDirectory = path;

        var files = new List<FileEntry>();
        long logicalBytes = 0;

        try
        {
            var entries = new FileSystemEnumerable<RawEntry>(
                path,
                static (ref FileSystemEntry e) => new RawEntry(
                    e.FileName.ToString(),
                    e.IsDirectory,
                    e.IsDirectory ? 0 : e.Length,
                    e.Attributes,
                    e.LastWriteTimeUtc.UtcDateTime),
                EnumerationOptions);

            foreach (RawEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    AddSubdirectory(node, path, entry);
                }
                else
                {
                    files.Add(CreateFileEntry(node, path, entry));
                    logicalBytes += entry.Length;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            node.State = DirectoryState.Unreadable;
            node.ErrorMessage = ex.Message;
            AddIssue(path, $"Ordner nicht (vollständig) lesbar: {ex.Message}");
        }

        node.Files = files.Count == 0 ? Array.Empty<FileEntry>() : files.ToArray();
        Interlocked.Add(ref _files, files.Count);
        Interlocked.Add(ref _logicalBytes, logicalBytes);
        Interlocked.Increment(ref _directories);
    }

    private void AddSubdirectory(DirectoryNode parent, string parentPath, RawEntry entry)
    {
        string childPath = Path.Join(parentPath, entry.Name);
        var child = new DirectoryNode(entry.Name, parent)
        {
            ProbeHardLinks = parent.ProbeHardLinks || IsProbeDirectory(childPath),
        };
        parent.AddChild(child);

        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // Cloud-Ordner (OneDrive) sind ebenfalls Reparse Points, aber weder Symlink noch Junction: betreten.
            try
            {
                string? target = new DirectoryInfo(childPath).LinkTarget;
                if (target is not null)
                {
                    child.State = DirectoryState.LinkSkipped;
                    child.LinkTarget = target;
                    return;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Unklar, ob Link: nicht betreten (Schleifengefahr) und melden.
                child.State = DirectoryState.LinkSkipped;
                AddIssue(childPath, $"Link-Prüfung fehlgeschlagen, Ordner übersprungen: {ex.Message}");
                return;
            }
        }

        Interlocked.Increment(ref _pending);
        _queue.Writer.TryWrite(child);
    }

    private FileEntry CreateFileEntry(DirectoryNode node, string directoryPath, RawEntry entry)
    {
        FileAttributes attributes = entry.Attributes;
        long length = entry.Length;
        long sizeOnDisk;
        ulong hardLinkId = 0;
        string? filePath = null;

        if ((attributes & CloudPlaceholder) != 0)
        {
            // Nur online vorhandene Datei: belegt lokal keinen Platz.
            sizeOnDisk = 0;
        }
        else if (attributes.HasFlag(FileAttributes.ReparsePoint) && IsLink(filePath ??= Path.Join(directoryPath, entry.Name)))
        {
            sizeOnDisk = 0;
        }
        else if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            filePath ??= Path.Join(directoryPath, entry.Name);
            sizeOnDisk = GetAllocatedSize(filePath, length);
        }
        else
        {
            sizeOnDisk = RoundUpToCluster(length);
        }

        if (node.ProbeHardLinks)
        {
            hardLinkId = ProbeHardLink(filePath ?? Path.Join(directoryPath, entry.Name));
        }

        return new FileEntry(entry.Name, length, sizeOnDisk, attributes, hardLinkId, IsHardLinkDuplicate: false, entry.LastWriteUtc);
    }

    private bool IsLink(string filePath)
    {
        try
        {
            return new FileInfo(filePath).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AddIssue(filePath, $"Link-Prüfung fehlgeschlagen, als Link gezählt: {ex.Message}");
            return true;
        }
    }

    private long RoundUpToCluster(long length)
        => length == 0 ? 0 : (length + _clusterSize - 1) / _clusterSize * _clusterSize;

    private long GetAllocatedSize(string filePath, long length)
    {
        uint low = NativeMethods.GetCompressedFileSizeW(NativeMethods.ToExtendedPath(filePath), out uint high);
        if (low == NativeMethods.InvalidFileSize)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                AddIssue(filePath, $"Belegung nicht ermittelbar, auf Clustergröße gerundet geschätzt: {new Win32Exception(error).Message}");
                return RoundUpToCluster(length);
            }
        }

        return ((long)high << 32) | low;
    }

    private ulong ProbeHardLink(string filePath)
    {
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            NativeMethods.ToExtendedPath(filePath),
            desiredAccess: 0,
            NativeMethods.FileShareAll,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid || !NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            AddIssue(filePath, $"Hardlink-Prüfung fehlgeschlagen, Datei voll gezählt: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return 0;
        }

        return info.NumberOfLinks > 1 ? ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow : 0;
    }

    private bool IsProbeDirectory(string path)
        => _probeDirectories.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase));

    private bool IsUnderProbeDirectory(string path)
        => _probeDirectories.Any(p =>
            string.Equals(path.TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Hardlinks in fester Baumreihenfolge entduplizieren, dann Summen von unten nach oben bilden.</summary>
    private static (long DuplicateFiles, long DuplicateBytes) Aggregate(DirectoryNode root)
    {
        var order = new List<DirectoryNode>();
        var stack = new Stack<DirectoryNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DirectoryNode node = stack.Pop();
            order.Add(node);
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        var seenLinks = new HashSet<ulong>();
        long duplicateFiles = 0;
        long duplicateBytes = 0;
        foreach (DirectoryNode node in order)
        {
            FileEntry[] files = node.Files;
            for (int i = 0; i < files.Length; i++)
            {
                if (files[i].HardLinkId != 0 && !seenLinks.Add(files[i].HardLinkId))
                {
                    duplicateFiles++;
                    duplicateBytes += files[i].SizeOnDisk;
                    files[i] = files[i] with { IsHardLinkDuplicate = true };
                }
            }
        }

        for (int n = order.Count - 1; n >= 0; n--)
        {
            DirectoryNode node = order[n];
            long onDisk = 0;
            long logical = 0;
            DateTime newest = DateTime.MinValue;
            foreach (FileEntry file in node.Files)
            {
                if (!file.IsHardLinkDuplicate)
                {
                    onDisk += file.SizeOnDisk;
                    logical += file.Length;
                    if (file.LastWriteUtc > newest)
                    {
                        newest = file.LastWriteUtc;
                    }
                }
            }

            long fileCount = node.Files.Length;
            long subdirectories = node.Children.Count;
            foreach (DirectoryNode child in node.Children)
            {
                onDisk += child.TotalSizeOnDisk;
                logical += child.TotalLogicalSize;
                fileCount += child.TotalFileCount;
                subdirectories += child.TotalSubdirectoryCount;
                if (child.NewestWriteUtc > newest)
                {
                    newest = child.NewestWriteUtc;
                }
            }

            node.NewestWriteUtc = newest;
            node.TotalSizeOnDisk = onDisk;
            node.TotalLogicalSize = logical;
            node.TotalFileCount = fileCount;
            node.TotalSubdirectoryCount = subdirectories;
        }

        return (duplicateFiles, duplicateBytes);
    }

    private static long GetClusterSize(string path)
    {
        string root = Path.GetPathRoot(path)
            ?? throw new ArgumentException($"Kein Laufwerk oder Netzwerkpfad: {path}", nameof(path));
        if (!root.EndsWith('\\'))
        {
            root += '\\';
        }

        if (!NativeMethods.GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Clustergröße von '{root}' nicht ermittelbar.");
        }

        return (long)sectorsPerCluster * bytesPerSector;
    }

    private void AddIssue(string path, string message) => _issues.Enqueue(new ScanIssue(path, message));

    private async Task ReportLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.ProgressInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                ReportProgress();
            }
        }
        catch (OperationCanceledException)
        {
            // Scan beendet.
        }
    }

    private void ReportProgress()
        => _progress?.Report(new ScanProgress(
            Interlocked.Read(ref _files),
            Interlocked.Read(ref _directories),
            Interlocked.Read(ref _logicalBytes),
            _issues.Count,
            _currentDirectory,
            _stopwatch.Elapsed));
}
