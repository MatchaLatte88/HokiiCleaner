using Cleanii.Core.Duplicates;
using Cleanii.Core.Scanning;

namespace Cleanii.Tests;

public class DuplicateFinderTests
{
    private static readonly DuplicateOptions Small = new() { MinLengthBytes = 1000 };

    private static string CreateWithContent(TempTree tree, string relativePath, byte[] content)
    {
        string path = tree.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i % 251);
        }

        return data;
    }

    [Fact]
    public async Task Finds_identical_files_in_groups_and_reports_the_reclaimable_space()
    {
        using var tree = new TempTree();
        byte[] a = Pattern(200_000, 1);
        byte[] b = Pattern(200_000, 2);
        CreateWithContent(tree, @"x\one.bin", a);
        CreateWithContent(tree, @"y\two.bin", a);
        CreateWithContent(tree, @"z\three.bin", a);
        CreateWithContent(tree, @"x\other1.bin", b);
        CreateWithContent(tree, @"y\other2.bin", b);
        CreateWithContent(tree, @"x\unique.bin", Pattern(50_000, 9));
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        DuplicateReport report = await DuplicateFinder.FindAsync(scan.Root, Small);

        Assert.Equal(2, report.Groups.Count);
        DuplicateGroup triple = report.Groups[0]; // größere Ersparnis zuerst
        Assert.Equal(3, triple.Files.Count);
        Assert.Equal(200_000, triple.Length);
        Assert.Equal(triple.SizeOnDisk * 2, triple.ReclaimableBytes);
        Assert.Equal(2, report.Groups[1].Files.Count);
        Assert.Equal(triple.ReclaimableBytes + report.Groups[1].ReclaimableBytes, report.ReclaimableBytes);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task Files_of_equal_size_that_differ_only_in_the_middle_are_not_duplicates()
    {
        using var tree = new TempTree();
        byte[] a = Pattern(400_000, 3);
        byte[] b = (byte[])a.Clone();
        b[200_000] ^= 0xFF; // Anfang und Ende gleich: erst der volle Hash unterscheidet
        CreateWithContent(tree, "a.bin", a);
        CreateWithContent(tree, "b.bin", b);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        DuplicateReport report = await DuplicateFinder.FindAsync(scan.Root, Small); // bewusst ohne Fortschrittsanzeige

        Assert.Empty(report.Groups);
        Assert.Equal(2, report.CandidateFiles);
        Assert.Equal(800_000, report.HashedBytes);
    }

    [Fact]
    public async Task Files_below_the_minimum_and_empty_files_are_ignored()
    {
        using var tree = new TempTree();
        byte[] small = Pattern(500, 1);
        CreateWithContent(tree, "s1.bin", small);
        CreateWithContent(tree, "s2.bin", small);
        CreateWithContent(tree, "e1.bin", Array.Empty<byte>());
        CreateWithContent(tree, "e2.bin", Array.Empty<byte>());
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        DuplicateReport report = await DuplicateFinder.FindAsync(scan.Root, Small);

        Assert.Empty(report.Groups);
        Assert.Equal(0, report.CandidateFiles);
    }

    [Fact]
    public async Task Hidden_files_are_skipped_unless_requested()
    {
        using var tree = new TempTree();
        byte[] content = Pattern(5000, 4);
        string visible = CreateWithContent(tree, "v.bin", content);
        string hidden = CreateWithContent(tree, "h.bin", content);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        DuplicateReport without = await DuplicateFinder.FindAsync(scan.Root, Small);
        DuplicateReport with = await DuplicateFinder.FindAsync(scan.Root, Small with { IncludeHidden = true });

        Assert.Empty(without.Groups);
        Assert.Single(with.Groups);
        Assert.True(File.Exists(visible));
    }

    [Fact]
    public async Task Hardlinks_to_one_file_are_not_reported_as_copies()
    {
        using var tree = new TempTree();
        string original = CreateWithContent(tree, @"store\movie.bin", Pattern(100_000, 5));
        TempTree.Run("cmd", $"/c mklink /H \"{tree.Combine("store", "alias.bin")}\" \"{original}\"");
        CreateWithContent(tree, @"real\copy.bin", Pattern(100_000, 5)); // echte zweite Kopie
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        DuplicateReport report = await DuplicateFinder.FindAsync(scan.Root, Small);

        DuplicateGroup group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Files.Count); // movie.bin/alias.bin zählen als eine Datei, dazu copy.bin
        Assert.Contains(group.Files, f => f.File.Name == "copy.bin");
    }

    [Fact]
    public async Task A_file_that_changed_after_the_scan_is_reported_not_compared()
    {
        using var tree = new TempTree();
        byte[] content = Pattern(50_000, 6);
        CreateWithContent(tree, "a.bin", content);
        string b = CreateWithContent(tree, "b.bin", content);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        File.AppendAllText(b, "mehr"); // Größe ändert sich nach dem Scan

        DuplicateReport report = await DuplicateFinder.FindAsync(scan.Root, Small);

        Assert.Empty(report.Groups);
        DuplicateIssue issue = Assert.Single(report.Issues);
        Assert.EndsWith("b.bin", issue.Path);
        Assert.Contains("geändert", issue.Message);
    }

    [Fact]
    public async Task A_locked_file_is_reported_as_an_issue_and_does_not_stop_the_run()
    {
        using var tree = new TempTree();
        byte[] content = Pattern(50_000, 7);
        CreateWithContent(tree, "a.bin", content);
        CreateWithContent(tree, "b.bin", content);
        string locked = CreateWithContent(tree, "c.bin", content);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        DuplicateReport report = await DuplicateFinder.FindAsync(scan.Root, Small);

        Assert.Equal(2, Assert.Single(report.Groups).Files.Count);
        DuplicateIssue issue = Assert.Single(report.Issues);
        Assert.EndsWith("c.bin", issue.Path);
    }

    [Fact]
    public async Task Progress_reaches_the_full_hash_stage_and_a_cancelled_run_throws()
    {
        using var tree = new TempTree();
        byte[] content = Pattern(80_000, 8);
        CreateWithContent(tree, "a.bin", content);
        CreateWithContent(tree, "b.bin", content);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        var stages = new List<DuplicateStage>();
        var progress = new SyncProgress(p => { lock (stages) { stages.Add(p.Stage); } });
        await DuplicateFinder.FindAsync(scan.Root, Small, progress);

        Assert.Contains(DuplicateStage.Collecting, stages);
        Assert.Contains(DuplicateStage.PartialHash, stages);
        Assert.Contains(DuplicateStage.FullHash, stages);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateFinder.FindAsync(scan.Root, Small, null, cts.Token));
    }

    [Fact]
    public async Task Invalid_options_are_rejected()
    {
        using var tree = new TempTree();
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DuplicateFinder.FindAsync(scan.Root, new DuplicateOptions { MinLengthBytes = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DuplicateFinder.FindAsync(scan.Root, new DuplicateOptions { Parallelism = 0 }));
    }

    /// <summary>Wie <see cref="Progress{T}"/>, aber ohne Umweg über den Thread-Pool, damit der Test sofort auswerten kann.</summary>
    private sealed class SyncProgress(Action<DuplicateProgress> handler) : IProgress<DuplicateProgress>
    {
        public void Report(DuplicateProgress value) => handler(value);
    }
}
