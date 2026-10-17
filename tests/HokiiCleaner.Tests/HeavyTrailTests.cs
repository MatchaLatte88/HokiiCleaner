using HokiiCleaner.Core.Categorization;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Tests;

public class HeavyTrailTests
{
    [Fact]
    public async Task Follows_dominant_folders_and_ends_in_the_dominant_file()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"big\sub\huge.bin", 10_000_000);
        tree.CreateFile(@"big\sub\small.txt", 1_000);
        tree.CreateFile(@"big\other.txt", 1_000);

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        DirectoryNode big = Assert.Single(scan.Root.Children);

        HeavyTrail? trail = HeavyTrail.Find(big);

        Assert.NotNull(trail);
        Assert.Equal(new[] { "sub", "huge.bin" }, trail.Names);
        Assert.True(trail.EndsInFile);
        Assert.True(trail.SizeOnDisk >= 10_000_000);
    }

    [Fact]
    public async Task Ends_at_a_folder_when_its_content_is_spread_out()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"top\wide\a.bin", 1_000_000);
        tree.CreateFile(@"top\wide\b.bin", 1_000_000);
        tree.CreateFile(@"top\wide\c.bin", 1_000_000);
        tree.CreateFile(@"top\tiny.txt", 1_000);

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        DirectoryNode top = Assert.Single(scan.Root.Children);

        HeavyTrail? trail = HeavyTrail.Find(top);

        Assert.NotNull(trail);
        Assert.Equal(new[] { "wide" }, trail.Names);
        Assert.False(trail.EndsInFile);
    }

    [Fact]
    public async Task Is_null_when_nothing_dominates_or_the_folder_is_empty()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"even\a.bin", 1_000_000);
        tree.CreateFile(@"even\b.bin", 1_000_000);
        tree.CreateDirectory("empty");

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        Assert.Null(HeavyTrail.Find(scan.Root.Children.Single(c => c.Name == "even")));
        Assert.Null(HeavyTrail.Find(scan.Root.Children.Single(c => c.Name == "empty")));
    }

    [Fact]
    public async Task Compose_lists_own_files_with_category_and_attaches_trails_to_children()
    {
        using var tree = new TempTree();
        tree.CreateFile("movie.mp4", 3_000_000);
        tree.CreateFile("notes.txt", 2_000);
        tree.CreateFile(@"models\big.safetensors", 8_000_000);

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        FolderComposition composition = CategoryAnalyzer.Compose(scan.Root, new CategoryAnalyzerOptions { RuleRoot = tree.Root });

        Assert.Equal(2, composition.OwnFiles.Count);
        Assert.Equal(FileCategory.Videos, composition.OwnFiles.Single(f => f.File.Name == "movie.mp4").Category);
        Assert.Equal(
            composition.OwnFilesSizeByCategory.Sum(),
            composition.OwnFiles.Sum(f => f.File.SizeOnDisk));

        ChildComposition models = Assert.Single(composition.Children);
        Assert.NotNull(models.Trail);
        Assert.Equal(new[] { "big.safetensors" }, models.Trail.Names);
    }
}
