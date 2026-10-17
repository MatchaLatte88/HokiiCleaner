using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Tests;

public class DiskScannerTests
{
    private sealed class CollectingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Reports { get; } = new();

        public void Report(ScanProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    private static long RoundUp(long length, long cluster)
        => length == 0 ? 0 : (length + cluster - 1) / cluster * cluster;

    [Fact]
    public async Task Sums_nested_files_with_logical_size_and_cluster_rounded_size_on_disk()
    {
        using var tree = new TempTree();
        tree.CreateFile("a.txt", 100);
        tree.CreateFile(@"sub\b.bin", 5000);
        tree.CreateFile(@"sub\deep\c.bin", 0);

        ScanResult result = await DiskScanner.ScanAsync(tree.Root);

        Assert.Equal(5100, result.Root.TotalLogicalSize);
        Assert.Equal(RoundUp(100, result.ClusterSize) + RoundUp(5000, result.ClusterSize), result.Root.TotalSizeOnDisk);
        Assert.Equal(3, result.Root.TotalFileCount);
        Assert.Equal(2, result.Root.TotalSubdirectoryCount);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Empty_directory_has_zero_totals()
    {
        using var tree = new TempTree();

        ScanResult result = await DiskScanner.ScanAsync(tree.Root);

        Assert.Equal(0, result.Root.TotalSizeOnDisk);
        Assert.Equal(0, result.Root.TotalFileCount);
        Assert.Equal(0, result.Root.TotalSubdirectoryCount);
    }

    [Fact]
    public async Task Hidden_and_system_files_are_counted()
    {
        using var tree = new TempTree();
        string hidden = tree.CreateFile("hidden.dat", 1000);
        string system = tree.CreateFile("system.dat", 2000);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.SetAttributes(system, FileAttributes.Hidden | FileAttributes.System);

        ScanResult result = await DiskScanner.ScanAsync(tree.Root);

        Assert.Equal(3000, result.Root.TotalLogicalSize);
        Assert.Equal(2, result.Root.TotalFileCount);
    }

    [Fact]
    public async Task Junction_is_not_followed_and_marked_as_skipped()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"real\big.bin", 1_000_000);
        tree.CreateJunction("link", "real");

        ScanResult result = await DiskScanner.ScanAsync(tree.Root);

        Assert.Equal(1_000_000, result.Root.TotalLogicalSize);
        Assert.Equal(1, result.Root.TotalFileCount);
        DirectoryNode link = Assert.Single(result.Root.Children, c => c.Name == "link");
        Assert.Equal(DirectoryState.LinkSkipped, link.State);
        Assert.NotNull(link.LinkTarget);
        Assert.Empty(link.Children);
    }

    [Fact]
    public async Task Hardlinks_in_probe_directory_are_counted_once()
    {
        using var tree = new TempTree();
        string original = tree.CreateFile(@"store\original.bin", 10_000);
        TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine("store", "second.bin")}\" \"{original}\"");
        TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine("other.bin")}\" \"{original}\"");

        var options = new ScanOptions { HardLinkProbeDirectories = new[] { tree.Root } };
        ScanResult result = await DiskScanner.ScanAsync(tree.Root, options);

        Assert.Equal(3, result.Root.TotalFileCount);
        Assert.Equal(10_000, result.Root.TotalLogicalSize);
        Assert.Equal(RoundUp(10_000, result.ClusterSize), result.Root.TotalSizeOnDisk);
        Assert.Equal(2, result.HardLinkDuplicateFiles);
    }

    [Fact]
    public async Task Hardlinks_outside_probe_directories_are_counted_in_full()
    {
        using var tree = new TempTree();
        string original = tree.CreateFile("original.bin", 10_000);
        TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine("second.bin")}\" \"{original}\"");

        var options = new ScanOptions { HardLinkProbeDirectories = Array.Empty<string>() };
        ScanResult result = await DiskScanner.ScanAsync(tree.Root, options);

        Assert.Equal(20_000, result.Root.TotalLogicalSize);
        Assert.Equal(0, result.HardLinkDuplicateFiles);
    }

    [Fact]
    public async Task Unreadable_directory_is_reported_and_not_dropped()
    {
        using var tree = new TempTree();
        tree.CreateFile("open.bin", 1000);
        string locked = tree.CreateDirectory("locked");
        tree.CreateFile(@"locked\hidden.bin", 5000);
        tree.DenyListing(locked);

        ScanResult result = await DiskScanner.ScanAsync(tree.Root);

        DirectoryNode node = Assert.Single(result.Root.Children, c => c.Name == "locked");
        Assert.Equal(DirectoryState.Unreadable, node.State);
        Assert.False(string.IsNullOrEmpty(node.ErrorMessage));
        Assert.Contains(result.Issues, i => i.Path == locked);
        Assert.Equal(1000, result.Root.TotalLogicalSize);
    }

    [Fact]
    public async Task Compressed_file_reports_allocated_size_below_logical_size()
    {
        using var tree = new TempTree();
        string file = tree.CreateFile("zeros.bin", 1_048_576);
        TempTree.Run("compact", $"/c \"{file}\"");

        ScanResult result = await DiskScanner.ScanAsync(tree.Root);

        FileEntry entry = Assert.Single(result.Root.Files);
        Assert.True(entry.Attributes.HasFlag(FileAttributes.Compressed));
        Assert.Equal(1_048_576, entry.Length);
        Assert.True(entry.SizeOnDisk < entry.Length, $"SizeOnDisk {entry.SizeOnDisk} nicht kleiner als {entry.Length}");
    }

    [Fact]
    public async Task Missing_root_throws()
    {
        using var tree = new TempTree();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => DiskScanner.ScanAsync(tree.Combine("does-not-exist")));
    }

    [Fact]
    public async Task Cancelled_scan_throws_instead_of_returning_partial_result()
    {
        using var tree = new TempTree();
        tree.CreateFile("a.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DiskScanner.ScanAsync(tree.Root, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Progress_final_report_matches_result()
    {
        using var tree = new TempTree();
        tree.CreateFile("a.bin", 10);
        tree.CreateFile(@"x\b.bin", 20);
        var progress = new CollectingProgress();

        ScanResult result = await DiskScanner.ScanAsync(tree.Root, progress: progress);

        ScanProgress last = progress.Reports[^1];
        Assert.Equal(2, last.Files);
        Assert.Equal(2, last.Directories);
        Assert.Equal(30, last.LogicalBytes);
        Assert.Equal(result.Root.TotalLogicalSize, last.LogicalBytes);
    }

    [Fact]
    public async Task Repeated_scans_give_identical_totals_regardless_of_parallelism()
    {
        using var tree = new TempTree();
        string original = tree.CreateFile(@"d0\f.bin", 4096);
        for (int d = 0; d < 25; d++)
        {
            for (int f = 0; f < 20; f++)
            {
                tree.CreateFile($@"d{d}\sub\file{f}.bin", 100 * (f + 1));
            }

            TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine($"d{d}", "link.bin")}\" \"{original}\"");
        }

        var serial = new ScanOptions { Parallelism = 1, HardLinkProbeDirectories = new[] { tree.Root } };
        var parallel = new ScanOptions { Parallelism = 8, HardLinkProbeDirectories = new[] { tree.Root } };

        ScanResult a = await DiskScanner.ScanAsync(tree.Root, serial);
        ScanResult b = await DiskScanner.ScanAsync(tree.Root, parallel);

        Assert.Equal(a.Root.TotalSizeOnDisk, b.Root.TotalSizeOnDisk);
        Assert.Equal(a.Root.TotalFileCount, b.Root.TotalFileCount);
        Assert.Equal(a.HardLinkDuplicateFiles, b.HardLinkDuplicateFiles);
        Assert.Equal(25, a.HardLinkDuplicateFiles);
        Assert.Equal(
            a.Root.Children.Select(c => (c.Name, c.TotalSizeOnDisk)),
            b.Root.Children.Select(c => (c.Name, c.TotalSizeOnDisk)));
    }
}
