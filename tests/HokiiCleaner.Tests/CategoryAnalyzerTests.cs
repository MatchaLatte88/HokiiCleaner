using HokiiCleaner.Core.Categorization;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Tests;

public class CategoryAnalyzerTests
{
    [Theory]
    [InlineData("movie.mp4", FileCategory.Videos)]
    [InlineData("PHOTO.JPG", FileCategory.Images)]
    [InlineData("song.flac", FileCategory.Audio)]
    [InlineData("report.pdf", FileCategory.Documents)]
    [InlineData("backup.zip", FileCategory.Archives)]
    [InlineData("archive.tar.gz", FileCategory.Archives)]
    [InlineData("setup.msi", FileCategory.Installers)]
    [InlineData("model.safetensors", FileCategory.AiModels)]
    [InlineData("disk.vhdx", FileCategory.VirtualDisks)]
    [InlineData("tool.exe", FileCategory.Apps)]
    [InlineData("unknown.xyz", FileCategory.Other)]
    [InlineData("noextension", FileCategory.Other)]
    [InlineData(".gitignore", FileCategory.Other)]
    [InlineData("trailingdot.", FileCategory.Other)]
    public void ByExtension_maps_known_and_unknown_names(string fileName, FileCategory expected)
    {
        Assert.Equal(expected, CategoryRules.Default.ByExtension(fileName));
    }

    private static async Task<(ScanResult Scan, string Root)> ScanSampleTree(TempTree tree)
    {
        tree.CreateFile(@"Windows\System32\kernel.dll", 1000);
        tree.CreateFile(@"Windows\Temp\a.tmp", 2000);
        tree.CreateFile(@"Windows\Logs\x.log", 3000);
        tree.CreateFile(@"Program Files\App\app.dll", 4000);
        tree.CreateFile(@"Program Files\App\data.mp4", 5000);
        tree.CreateFile(@"Users\bob\AppData\Local\Temp\t.mp4", 6000);
        tree.CreateFile(@"Users\bob\AppData\Local\Programs\Tool\t.bin", 7000);
        tree.CreateFile(@"Users\bob\Videos\v.mp4", 8000);
        tree.CreateFile(@"Users\bob\AppData\Local\Foo\Cache\blob.bin", 9000);
        tree.CreateFile(@"Users\bob\AppData\Local\Foo\Cache\Inner\x.bin", 10000);
        tree.CreateFile(@"$Recycle.Bin\S-1\$R1.mp4", 11000);
        tree.CreateFile(@"Windows.old\Users\x.docx", 12000);
        tree.CreateFile("hiberfil.sys", 13000);
        tree.CreateFile("readme.txt", 14000);
        tree.CreateFile(@"Users\bob\Documents\notes.txt", 15000);

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        return (scan, tree.Root);
    }

    [Fact]
    public async Task Path_rules_override_extensions_and_deepest_rule_wins()
    {
        using var tree = new TempTree();
        var (scan, root) = await ScanSampleTree(tree);

        CategoryBreakdown result = CategoryAnalyzer.Analyze(scan.Root, new CategoryAnalyzerOptions { RuleRoot = root });

        Assert.Equal(14000, result[FileCategory.System].LogicalSize);        // Windows\* und hiberfil.sys im Wurzelordner
        Assert.Equal(30000, result[FileCategory.TempAndCaches].LogicalSize); // Windows\Temp, Windows\Logs, AppData\Local\Temp, Cache-Ordner samt Unterordner
        Assert.Equal(16000, result[FileCategory.Apps].LogicalSize);          // Program Files (auch .mp4) und AppData\Local\Programs
        Assert.Equal(8000, result[FileCategory.Videos].LogicalSize);
        Assert.Equal(11000, result[FileCategory.RecycleBin].LogicalSize);
        Assert.Equal(12000, result[FileCategory.OldWindows].LogicalSize);
        Assert.Equal(29000, result[FileCategory.Documents].LogicalSize);
        Assert.Equal(0, result[FileCategory.Other].LogicalSize);
        Assert.Equal(120000, result.TotalLogicalSize);
    }

    [Fact]
    public async Task Analyzing_a_subtree_keeps_the_rules_of_its_path()
    {
        using var tree = new TempTree();
        var (scan, root) = await ScanSampleTree(tree);
        DirectoryNode windows = Assert.Single(scan.Root.Children, c => c.Name == "Windows");

        CategoryBreakdown result = CategoryAnalyzer.Analyze(windows, new CategoryAnalyzerOptions { RuleRoot = root });

        Assert.Equal(1000, result[FileCategory.System].LogicalSize);
        Assert.Equal(5000, result[FileCategory.TempAndCaches].LogicalSize);
        Assert.Equal(6000, result.TotalLogicalSize);
    }

    [Fact]
    public async Task Root_file_rule_applies_only_in_the_rule_root()
    {
        using var tree = new TempTree();
        tree.CreateFile("pagefile.sys", 1000);
        tree.CreateFile(@"sub\pagefile.sys", 2000);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        CategoryBreakdown result = CategoryAnalyzer.Analyze(scan.Root, new CategoryAnalyzerOptions { RuleRoot = tree.Root });

        Assert.Equal(1000, result[FileCategory.System].LogicalSize);
        Assert.Equal(2000, result[FileCategory.Other].LogicalSize);
    }

    [Fact]
    public async Task Category_sum_equals_scan_total_with_hardlink_duplicates_and_junctions()
    {
        using var tree = new TempTree();
        string original = tree.CreateFile(@"store\movie.mp4", 3_000_000);
        TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine("store", "copy.mp4")}\" \"{original}\"");
        tree.CreateFile(@"real\doc.pdf", 50_000);
        tree.CreateJunction("link", "real");
        tree.CreateFile("other.xyz", 123);

        var scanOptions = new ScanOptions { HardLinkProbeDirectories = new[] { tree.Root } };
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root, scanOptions);
        CategoryBreakdown result = CategoryAnalyzer.Analyze(scan.Root, new CategoryAnalyzerOptions { RuleRoot = tree.Root });

        Assert.Equal(1, scan.HardLinkDuplicateFiles);
        Assert.Equal(scan.Root.TotalSizeOnDisk, result.TotalSizeOnDisk);
        Assert.Equal(scan.Root.TotalLogicalSize, result.TotalLogicalSize);
        Assert.Equal(1, result[FileCategory.Videos].FileCount);
    }

    [Fact]
    public async Task Top_files_are_sorted_descending_and_limited()
    {
        using var tree = new TempTree();
        for (int i = 1; i <= 5; i++)
        {
            tree.CreateFile($@"d{i}\v{i}.mp4", i * 1_048_576L);
        }

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        CategoryBreakdown result = CategoryAnalyzer.Analyze(
            scan.Root,
            new CategoryAnalyzerOptions { RuleRoot = tree.Root, TopFilesPerCategory = 3 });

        IReadOnlyList<TopFile> top = result.TopFiles(FileCategory.Videos);
        Assert.Equal(new[] { 5L, 4L, 3L }, top.Select(t => t.File.Length / 1_048_576L).ToArray());
        Assert.Equal(tree.Combine("d5", "v5.mp4"), top[0].FullPath);
        Assert.Empty(result.TopFiles(FileCategory.Images));
    }

    [Fact]
    public async Task Rule_root_that_is_not_an_ancestor_throws()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"sub\a.bin", 1);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        DirectoryNode sub = Assert.Single(scan.Root.Children);

        Assert.Throws<ArgumentException>(
            () => CategoryAnalyzer.Analyze(sub, new CategoryAnalyzerOptions { RuleRoot = tree.Combine("elsewhere") }));
    }
}
