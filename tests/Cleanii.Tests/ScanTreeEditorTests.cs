using Cleanii.Core.Scanning;

namespace Cleanii.Tests;

/// <summary>Tests für das Nachführen des Scan-Baums, wenn Cleanii Dateien in den Papierkorb verschoben hat.</summary>
public class ScanTreeEditorTests
{
    private static async Task<(TempTree Tree, ScanResult Scan)> ScanAsync(Action<TempTree> build)
    {
        var tree = new TempTree();
        build(tree);
        return (tree, await DiskScanner.ScanAsync(tree.Root));
    }

    [Fact]
    public async Task Removing_a_file_subtracts_it_from_every_ancestor()
    {
        var (tree, scan) = await ScanAsync(t =>
        {
            t.CreateFile(@"a\big.bin", 100_000);
            t.CreateFile(@"a\deep\small.bin", 5_000);
            t.CreateFile(@"b\other.bin", 7_000);
        });
        using (tree)
        {
            DirectoryNode a = scan.Root.Children.Single(c => c.Name == "a");
            long rootBefore = scan.Root.TotalSizeOnDisk;
            long aBefore = a.TotalSizeOnDisk;
            FileEntry big = a.Files.Single();

            RemovedEntry? removed = ScanTreeEditor.Remove(scan.Root, tree.Combine("a", "big.bin"));

            Assert.NotNull(removed);
            Assert.False(removed.IsDirectory);
            Assert.Equal(big.SizeOnDisk, removed.SizeOnDisk);
            Assert.Equal(rootBefore - big.SizeOnDisk, scan.Root.TotalSizeOnDisk);
            Assert.Equal(aBefore - big.SizeOnDisk, a.TotalSizeOnDisk);
            Assert.Equal(2, scan.Root.TotalFileCount);
            Assert.Empty(a.Files);
        }
    }

    [Fact]
    public async Task Removing_a_folder_subtracts_its_whole_subtree_and_the_newest_time_follows()
    {
        var (tree, scan) = await ScanAsync(t =>
        {
            t.CreateFile(@"keep\old.bin", 1_000);
            t.CreateFile(@"drop\x\new.bin", 2_000);
            t.CreateFile(@"drop\y.bin", 3_000);
        });
        using (tree)
        {
            var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var newTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(tree.Combine("keep", "old.bin"), oldTime);
            File.SetLastWriteTimeUtc(tree.Combine("drop", "x", "new.bin"), newTime);
            File.SetLastWriteTimeUtc(tree.Combine("drop", "y.bin"), new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            scan = await DiskScanner.ScanAsync(tree.Root);
            DirectoryNode drop = scan.Root.Children.Single(c => c.Name == "drop");
            long dropSize = drop.TotalSizeOnDisk;
            long rootSize = scan.Root.TotalSizeOnDisk;
            Assert.Equal(newTime, scan.Root.NewestWriteUtc);

            RemovedEntry? removed = ScanTreeEditor.Remove(scan.Root, tree.Combine("drop"));

            Assert.NotNull(removed);
            Assert.True(removed.IsDirectory);
            Assert.Equal(2, removed.FileCount);
            Assert.Equal(rootSize - dropSize, scan.Root.TotalSizeOnDisk);
            Assert.Equal(1, scan.Root.TotalFileCount);
            Assert.Equal(1, scan.Root.TotalSubdirectoryCount); // nur "keep"
            Assert.Equal(oldTime, scan.Root.NewestWriteUtc);
            Assert.DoesNotContain(drop, scan.Root.Children);
            Assert.False(ScanTreeEditor.IsAttached(scan.Root, drop));
        }
    }

    [Fact]
    public async Task Unknown_paths_return_null_and_the_root_cannot_be_removed()
    {
        var (tree, scan) = await ScanAsync(t => t.CreateFile(@"a\f.bin", 10));
        using (tree)
        {
            Assert.Null(ScanTreeEditor.Remove(scan.Root, tree.Combine("a", "missing.bin")));
            Assert.Null(ScanTreeEditor.Remove(scan.Root, tree.Combine("nope")));
            Assert.Null(ScanTreeEditor.Remove(scan.Root, @"X:\ausserhalb\datei.bin"));
            Assert.Throws<ArgumentException>(() => ScanTreeEditor.Remove(scan.Root, tree.Root));
        }
    }

    [Fact]
    public async Task ReplaceContents_swaps_a_folder_for_a_fresh_read_and_fixes_the_totals_above()
    {
        var (tree, scan) = await ScanAsync(t =>
        {
            t.CreateFile(@"bin\old\a.bin", 4_000);
            t.CreateFile(@"keep\k.bin", 1_000);
        });
        using (tree)
        {
            DirectoryNode bin = scan.Root.Children.Single(c => c.Name == "bin");
            long keep = scan.Root.Children.Single(c => c.Name == "keep").TotalSizeOnDisk;

            // Der Ordner ändert sich auf der Platte: alte Unterordner weg, neue Dateien dazu.
            Directory.Delete(tree.Combine("bin", "old"), recursive: true);
            tree.CreateFile(@"bin\r1\x.bin", 9_000);
            tree.CreateFile(@"bin\r1\deep\y.bin", 2_000);
            tree.CreateFile(@"bin\direct.bin", 5_000);
            ScanResult fresh = await DiskScanner.ScanAsync(tree.Combine("bin"));

            ScanTreeEditor.ReplaceContents(bin, fresh.Root);

            Assert.Equal(fresh.Root.TotalSizeOnDisk, bin.TotalSizeOnDisk);
            Assert.Equal(3, bin.TotalFileCount);
            Assert.Equal(2, bin.TotalSubdirectoryCount);
            Assert.Equal(keep + fresh.Root.TotalSizeOnDisk, scan.Root.TotalSizeOnDisk);
            Assert.Equal(4, scan.Root.TotalFileCount);
            Assert.NotNull(scan.Root.FindDescendant(tree.Combine("bin", "r1", "deep")));
            Assert.Null(scan.Root.FindDescendant(tree.Combine("bin", "old")));
            Assert.Equal("direct.bin", Assert.Single(bin.Files).Name);
            Assert.True(ScanTreeEditor.IsAttached(scan.Root, scan.Root.FindDescendant(tree.Combine("bin", "r1", "deep"))!));
            Assert.NotSame(fresh.Root.Children.First(), bin.Children.First()); // eine Kopie, nicht dieselben Knoten
        }
    }

    [Fact]
    public async Task EnsureFolder_creates_missing_levels_and_counts_them()
    {
        var (tree, scan) = await ScanAsync(t => t.CreateFile(@"a\f.bin", 10));
        using (tree)
        {
            long subdirectories = scan.Root.TotalSubdirectoryCount;

            DirectoryNode? created = ScanTreeEditor.EnsureFolder(scan.Root, tree.Combine("x", "y", "z"));

            Assert.NotNull(created);
            Assert.Equal("z", created.Name);
            Assert.Equal(subdirectories + 3, scan.Root.TotalSubdirectoryCount);
            Assert.Same(created, ScanTreeEditor.EnsureFolder(scan.Root, tree.Combine("x", "y", "z")));
            Assert.Equal(subdirectories + 3, scan.Root.TotalSubdirectoryCount);
            Assert.Null(ScanTreeEditor.EnsureFolder(scan.Root, @"X:\woanders"));
        }
    }

    [Fact]
    public async Task ClearContents_empties_a_folder_and_the_totals_above_it()
    {
        var (tree, scan) = await ScanAsync(t =>
        {
            t.CreateFile(@"bin\r1\a.bin", 4_000);
            t.CreateFile(@"bin\r2.bin", 8_000);
            t.CreateFile(@"keep\k.bin", 1_000);
        });
        using (tree)
        {
            DirectoryNode bin = scan.Root.Children.Single(c => c.Name == "bin");
            long keep = scan.Root.Children.Single(c => c.Name == "keep").TotalSizeOnDisk;

            ScanTreeEditor.ClearContents(bin);

            Assert.Equal(0, bin.TotalSizeOnDisk);
            Assert.Equal(0, bin.TotalFileCount);
            Assert.Equal(0, bin.TotalSubdirectoryCount);
            Assert.Empty(bin.Children);
            Assert.Empty(bin.Files);
            Assert.Equal(keep, scan.Root.TotalSizeOnDisk);
            Assert.Equal(1, scan.Root.TotalFileCount);
        }
    }

    [Fact]
    public async Task Readers_that_hold_the_old_child_list_are_not_disturbed()
    {
        var (tree, scan) = await ScanAsync(t =>
        {
            t.CreateFile(@"a\f.bin", 10);
            t.CreateFile(@"b\f.bin", 10);
        });
        using (tree)
        {
            IReadOnlyList<DirectoryNode> held = scan.Root.Children;
            int before = held.Count;

            ScanTreeEditor.Remove(scan.Root, tree.Combine("a"));

            Assert.Equal(before, held.Count); // die gehaltene Liste bleibt unverändert
            Assert.Equal(before - 1, scan.Root.Children.Count);
        }
    }
}
