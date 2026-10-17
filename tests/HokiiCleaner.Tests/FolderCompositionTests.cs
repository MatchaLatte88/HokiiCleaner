using HokiiCleaner.Core.Categorization;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Tests;

public class FolderCompositionTests
{
    private static long RoundUp(long length, long cluster)
        => length == 0 ? 0 : (length + cluster - 1) / cluster * cluster;

    [Fact]
    public async Task Children_and_own_files_add_up_to_the_folder_and_to_Analyze()
    {
        using var tree = new TempTree();
        tree.CreateFile("own.mp4", 3_000_000);
        tree.CreateFile(@"Windows\kernel.dll", 1_000_000);                // Pfadregel: System
        tree.CreateFile(@"Windows\Temp\x.tmp", 2_000_000);                // tiefere Regel: Temp
        tree.CreateFile(@"media\a.mp4", 4_000_000);
        tree.CreateFile(@"media\b.jpg", 1_500_000);
        tree.CreateFile(@"media\deep\c.mp3", 500_000);
        tree.CreateFile(@"empty\placeholder.xyz", 0);

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        var options = new CategoryAnalyzerOptions { RuleRoot = tree.Root };

        FolderComposition composition = CategoryAnalyzer.Compose(scan.Root, options);
        CategoryBreakdown breakdown = CategoryAnalyzer.Analyze(scan.Root, options);

        Assert.Equal(scan.Root.TotalSizeOnDisk, composition.TotalSizeOnDisk);
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            Assert.Equal(breakdown[category].SizeOnDisk, composition.SizeByCategory[(int)category]);
        }

        Assert.Equal(RoundUp(3_000_000, scan.ClusterSize), composition.OwnFilesSizeOf(FileCategory.Videos));
        Assert.Equal(FileCategory.Videos, composition.OwnFilesDominant);
        Assert.Equal(3, composition.Children.Count);

        ChildComposition windows = Assert.Single(composition.Children, c => c.Node.Name == "Windows");
        Assert.Equal(RoundUp(1_000_000, scan.ClusterSize), windows.SizeOf(FileCategory.System));
        Assert.Equal(RoundUp(2_000_000, scan.ClusterSize), windows.SizeOf(FileCategory.TempAndCaches));
        Assert.Equal(FileCategory.TempAndCaches, windows.Dominant);

        ChildComposition media = Assert.Single(composition.Children, c => c.Node.Name == "media");
        Assert.Equal(media.Node.TotalSizeOnDisk, media.TotalSizeOnDisk);
        Assert.Equal(FileCategory.Videos, media.Dominant);

        ChildComposition empty = Assert.Single(composition.Children, c => c.Node.Name == "empty");
        Assert.Equal(0, empty.TotalSizeOnDisk);
        Assert.Null(empty.Dominant);
    }

    [Fact]
    public async Task Composition_excludes_hardlink_duplicates_and_junction_content()
    {
        using var tree = new TempTree();
        string original = tree.CreateFile(@"store\movie.mp4", 3_000_000);
        TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine("store", "copy.mp4")}\" \"{original}\"");
        tree.CreateFile(@"real\doc.pdf", 50_000);
        tree.CreateJunction("link", "real");

        var scanOptions = new ScanOptions { HardLinkProbeDirectories = new[] { tree.Root } };
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root, scanOptions);
        FolderComposition composition = CategoryAnalyzer.Compose(scan.Root, new CategoryAnalyzerOptions { RuleRoot = tree.Root });

        Assert.Equal(scan.Root.TotalSizeOnDisk, composition.TotalSizeOnDisk);
        foreach (ChildComposition child in composition.Children)
        {
            Assert.Equal(child.Node.TotalSizeOnDisk, child.TotalSizeOnDisk);
        }

        ChildComposition link = Assert.Single(composition.Children, c => c.Node.Name == "link");
        Assert.Equal(0, link.TotalSizeOnDisk);
    }

    [Fact]
    public async Task Composition_of_a_subfolder_keeps_the_rules_of_its_path()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"Windows\System32\kernel.dll", 100_000);
        tree.CreateFile(@"Windows\Temp\x.tmp", 200_000);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        DirectoryNode windows = Assert.Single(scan.Root.Children);

        FolderComposition composition = CategoryAnalyzer.Compose(windows, new CategoryAnalyzerOptions { RuleRoot = tree.Root });

        ChildComposition system32 = Assert.Single(composition.Children, c => c.Node.Name == "System32");
        Assert.Equal(RoundUp(100_000, scan.ClusterSize), system32.SizeOf(FileCategory.System));
        ChildComposition temp = Assert.Single(composition.Children, c => c.Node.Name == "Temp");
        Assert.Equal(RoundUp(200_000, scan.ClusterSize), temp.SizeOf(FileCategory.TempAndCaches));
    }
}
