using Cleanii.Core.Categorization;
using Cleanii.Core.Scanning;

namespace Cleanii.Tests;

public class FileRankingTests
{
    [Fact]
    public async Task Largest_returns_the_biggest_files_of_the_whole_subtree_in_descending_order()
    {
        using var tree = new TempTree();
        for (int i = 1; i <= 6; i++)
        {
            tree.CreateFile($@"d{i}\deep\f{i}.bin", i * 1_048_576L);
        }

        tree.CreateFile("small.bin", 10);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        IReadOnlyList<TopFile> top = FileRanking.Largest(scan.Root, 3);

        Assert.Equal(new[] { 6L, 5L, 4L }, top.Select(t => t.File.Length / 1_048_576L).ToArray());
        Assert.Equal(tree.Combine("d6", "deep", "f6.bin"), top[0].FullPath);
    }

    [Fact]
    public async Task Largest_covers_only_the_given_subtree()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"a\big.bin", 5_000_000);
        tree.CreateFile(@"b\other.bin", 9_000_000);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        DirectoryNode a = Assert.Single(scan.Root.Children, c => c.Name == "a");

        TopFile only = Assert.Single(FileRanking.Largest(a, 5));

        Assert.Equal("big.bin", only.File.Name);
    }

    [Fact]
    public async Task Largest_skips_hardlink_duplicates()
    {
        using var tree = new TempTree();
        string original = tree.CreateFile(@"store\movie.bin", 3_000_000);
        TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine("store", "copy.bin")}\" \"{original}\"");

        var options = new ScanOptions { HardLinkProbeDirectories = new[] { tree.Root } };
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root, options);

        Assert.Single(FileRanking.Largest(scan.Root, 10));
    }

    [Fact]
    public async Task Largest_handles_zero_count_empty_folders_and_rejects_negative_counts()
    {
        using var tree = new TempTree();
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        Assert.Empty(FileRanking.Largest(scan.Root, 0));
        Assert.Empty(FileRanking.Largest(scan.Root, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileRanking.Largest(scan.Root, -1));
    }
}
