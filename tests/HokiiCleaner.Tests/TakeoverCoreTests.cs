using System.Text.Json;
using HokiiCleaner.Core.Categorization;
using HokiiCleaner.Core.Diagnostics;
using HokiiCleaner.Core.Export;
using HokiiCleaner.Core.Programs;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Tests;

/// <summary>Tests für Änderungsdatum, Pfad-Regeln, Suche, Export und Programmliste.</summary>
public class TakeoverCoreTests
{
    // ------------------------------------------------------------ A1 Änderungsdatum

    [Fact]
    public async Task Scan_records_the_last_write_time_and_folders_know_their_newest_file()
    {
        using var tree = new TempTree();
        string old = tree.CreateFile(@"a\old.bin", 10);
        string recent = tree.CreateFile(@"a\deep\recent.bin", 10);
        tree.CreateFile(@"b\only.bin", 10);
        var oldTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var recentTime = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        var onlyTime = new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(old, oldTime);
        File.SetLastWriteTimeUtc(recent, recentTime);
        File.SetLastWriteTimeUtc(tree.Combine("b", "only.bin"), onlyTime);

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        DirectoryNode a = scan.Root.Children.Single(c => c.Name == "a");
        Assert.Equal(oldTime, Assert.Single(a.Files).LastWriteUtc);
        Assert.Equal(recentTime, a.NewestWriteUtc);
        Assert.Equal(onlyTime, scan.Root.Children.Single(c => c.Name == "b").NewestWriteUtc);
        Assert.Equal(recentTime, scan.Root.NewestWriteUtc);
    }

    [Fact]
    public async Task An_empty_folder_has_no_newest_time()
    {
        using var tree = new TempTree();
        tree.CreateDirectory("empty");

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        Assert.Equal(DateTime.MinValue, Assert.Single(scan.Root.Children).NewestWriteUtc);
    }

    [Fact]
    public async Task FindDescendant_resolves_paths_case_insensitively_and_rejects_outsiders()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"One\Two\f.bin", 1);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        DirectoryNode? two = scan.Root.FindDescendant(tree.Combine("one", "TWO"));

        Assert.NotNull(two);
        Assert.Equal("Two", two.Name);
        Assert.Same(scan.Root, scan.Root.FindDescendant(tree.Root));
        Assert.Null(scan.Root.FindDescendant(tree.Combine("One", "Missing")));
        Assert.Null(scan.Root.FindDescendant(tree.Root + "-other"));
        Assert.Null(scan.Root.FindDescendant(Path.GetTempPath()));
    }

    // ------------------------------------------------------------ A6 Regeln

    [Fact]
    public async Task New_path_rules_classify_known_system_caches_but_leave_neighbours_alone()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"Windows\MEMORY.DMP", 1000);
        tree.CreateFile(@"Windows\other.dmp", 2000);
        tree.CreateFile(@"Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache\x.bin", 3000);
        tree.CreateFile(@"Windows\ServiceProfiles\LocalService\AppData\Local\FontCache\f.dat", 4000);
        tree.CreateFile(@"Windows\SoftwareDistribution\DeliveryOptimization\y.bin", 5000);
        tree.CreateFile(@"ProgramData\Microsoft\Windows\WER\ReportQueue\r.wer", 6000);
        tree.CreateFile(@"ProgramData\Other\keep.dat", 7000);
        tree.CreateFile(@"Users\bob\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db", 8000);
        tree.CreateFile(@"Users\bob\AppData\Local\Microsoft\Windows\WER\w.wer", 9000);
        tree.CreateFile(@"found.000\file0000.chk", 10000);

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        CategoryBreakdown result = CategoryAnalyzer.Analyze(scan.Root, new CategoryAnalyzerOptions { RuleRoot = tree.Root });

        Assert.Equal(1000 + 3000 + 4000 + 5000 + 6000 + 8000 + 9000 + 10000, result[FileCategory.TempAndCaches].LogicalSize);
        Assert.Equal(2000, result[FileCategory.System].LogicalSize);   // Windows\other.dmp bleibt System
        Assert.Equal(7000, result[FileCategory.Apps].LogicalSize);     // ProgramData\Other bleibt Apps
    }

    [Fact]
    public void AddPathFile_needs_a_folder_and_a_file_name()
    {
        Assert.Throws<ArgumentException>(() => new CategoryRules().AddPathFile(FileCategory.System, "only-one"));
    }

    [Fact]
    public async Task CategorizeFile_agrees_with_Compose_for_every_file()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"Windows\MEMORY.DMP", 1000);
        tree.CreateFile(@"Windows\k.dll", 2000);
        tree.CreateFile(@"Users\bob\Videos\v.mp4", 3000);
        tree.CreateFile(@"Users\bob\AppData\Local\Foo\Cache\blob.bin", 4000);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);
        var options = new CategoryAnalyzerOptions { RuleRoot = tree.Root };

        var stack = new Stack<DirectoryNode>(new[] { scan.Root });
        int checkedFiles = 0;
        while (stack.Count > 0)
        {
            DirectoryNode folder = stack.Pop();
            FolderComposition composition = CategoryAnalyzer.Compose(folder, options);
            foreach (OwnFile own in composition.OwnFiles)
            {
                Assert.Equal(own.Category, CategoryAnalyzer.CategorizeFile(folder, own.File, options));
                checkedFiles++;
            }

            foreach (DirectoryNode child in folder.Children)
            {
                stack.Push(child);
            }
        }

        Assert.Equal(4, checkedFiles);
    }

    // ------------------------------------------------------------ A3 Suche

    [Fact]
    public async Task Search_finds_files_and_folders_by_name_part_and_ranks_by_size()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"movies\Holiday.mp4", 3_000_000);
        tree.CreateFile(@"movies\sub\holiday-small.mp4", 100_000);
        tree.CreateFile(@"HOLIDAY-photos\a.jpg", 2_000_000);
        tree.CreateFile(@"other\x.txt", 10);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        SearchResult result = TreeSearch.Find(scan.Root, "holiday", limit: 10);

        Assert.Equal(3, result.TotalMatches);
        Assert.Equal(new[] { "Holiday.mp4", "HOLIDAY-photos", "holiday-small.mp4" },
            result.Hits.Select(h => h.File?.Name ?? h.Folder.Name));
        Assert.True(result.Hits[0].IsFile);
        Assert.False(result.Hits[1].IsFile);
        Assert.Equal(result.Hits.Select(h => h.SizeOnDisk).OrderByDescending(x => x), result.Hits.Select(h => h.SizeOnDisk));
    }

    [Fact]
    public async Task Search_keeps_only_the_largest_hits_but_counts_all_matches()
    {
        using var tree = new TempTree();
        for (int i = 1; i <= 6; i++)
        {
            tree.CreateFile($"f\\hit{i}.bin", i * 100_000);
        }

        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        SearchResult result = TreeSearch.Find(scan.Root, "hit", limit: 3);

        Assert.Equal(6, result.TotalMatches);
        Assert.Equal(new[] { "hit6.bin", "hit5.bin", "hit4.bin" }, result.Hits.Select(h => h.File!.Value.Name));
    }

    [Fact]
    public async Task Search_without_matches_is_empty_and_a_cancelled_search_throws()
    {
        using var tree = new TempTree();
        tree.CreateFile("a.bin", 1);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        SearchResult none = TreeSearch.Find(scan.Root, "zzz", 5);
        Assert.Empty(none.Hits);
        Assert.Equal(0, none.TotalMatches);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => TreeSearch.Find(scan.Root, "a", 5, cts.Token));
        Assert.Throws<ArgumentException>(() => TreeSearch.Find(scan.Root, string.Empty, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TreeSearch.Find(scan.Root, "a", 0));
    }

    // ------------------------------------------------------------ A2 Export

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("", "")]
    public void Csv_escapes_separators_quotes_and_formula_starters(string input, string expected)
    {
        Assert.Equal(expected, CsvWriter.Escape(input));
    }

    [Fact]
    public void Csv_writes_header_and_rows_with_crlf_and_rejects_rows_of_the_wrong_width()
    {
        var writer = new StringWriter();

        CsvWriter.Write(writer, new[] { "Name", "Größe" }, new[] { (IReadOnlyList<string>)new[] { "a;b", "10" } });

        Assert.Equal("Name;Größe\r\n\"a;b\";10\r\n", writer.ToString());
        Assert.Throws<ArgumentException>(() =>
            CsvWriter.Write(new StringWriter(), new[] { "x", "y" }, new[] { (IReadOnlyList<string>)new[] { "only-one" } }));
        Assert.True(CsvWriter.Encoding.GetPreamble().Length == 3, "UTF-8 mit BOM erwartet");
    }

    [Fact]
    public void Report_json_contains_every_check_with_threshold_and_source()
    {
        var checks = new[]
        {
            new Check("drive.free.C:\\", CheckArea.Drive, CheckOutcome.Warning, "Freier Platz", "12 % frei", "Warnung unter 15 %", "Erklärung mit Umlauten äöü", "DriveInfo", new[] { "Detail 1" }),
            new Check("drive.smart", CheckArea.Drive, CheckOutcome.Unavailable, "S.M.A.R.T.", "Nicht gemessen", null, "Grund", "WMI", Array.Empty<string>()),
        };
        var snapshot = SnapshotOf(elevated: true);
        var report = new SlownessReport(snapshot, checks, TimeSpan.FromSeconds(7.25));

        string json = ReportJson.Serialize(report, new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("HokiiCleaner", root.GetProperty("Tool").GetString());
        Assert.True(root.GetProperty("Administratorrechte").GetBoolean());
        JsonElement items = root.GetProperty("Befunde");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Warning", items[0].GetProperty("Ergebnis").GetString());
        Assert.Equal("Warnung unter 15 %", items[0].GetProperty("Threshold").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("Threshold").ValueKind);
        Assert.Contains("äöü", json); // Umlaute bleiben lesbar
    }

    private static SlownessSnapshot SnapshotOf(bool elevated)
    {
        static Measured<T> No<T>() => Measured<T>.Unavailable("im Test nicht gemessen");
        return new SlownessSnapshot(
            elevated,
            No<IReadOnlyList<VolumeInfo>>(), No<IReadOnlyList<PhysicalDiskInfo>>(), No<DiskActivity>(),
            No<IReadOnlyList<StartupEntry>>(), No<IReadOnlyList<ScheduledTaskEntry>>(), No<IReadOnlyList<ServiceEntry>>(),
            No<ProcessSampleResult>(), No<MemoryInfo>(), No<SystemConfig>(), No<IReadOnlyList<DisplayDriverInfo>>(),
            No<IReadOnlyList<ShellExtensionInfo>>(), No<IReadOnlyList<BrowserExtensionInfo>>(), No<IReadOnlyList<DriverInfo>>(),
            No<ShadowStorageInfo>());
    }

    // ------------------------------------------------------------ B4 Programme

    [Fact]
    public async Task Programs_get_the_measured_size_of_their_folder_and_are_sorted_by_it()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"Apps\Big\data.bin", 5_000_000);
        tree.CreateFile(@"Apps\Small\data.bin", 100_000);
        ScanResult scan = await DiskScanner.ScanAsync(tree.Root);

        var programs = new[]
        {
            new InstalledProgram("Small", "Pub", "1.0", tree.Combine("Apps", "Small"), false, 999_999_999, null, "test"),
            new InstalledProgram("NotScanned", null, null, @"Z:\gone", false, 1234, null, "test"),
            new InstalledProgram("Big", null, null, tree.Combine("apps", "BIG"), false, null, null, "test"),
            new InstalledProgram("NoFolder", null, null, null, false, null, null, "test"),
        };

        IReadOnlyList<ProgramSize> sizes = InstalledProgramsProbe.Measure(programs, scan.Root);

        Assert.Equal(new[] { "Big", "Small", "NotScanned", "NoFolder" }, sizes.Select(s => s.Program.Name));
        Assert.True(sizes[0].IsMeasured);
        Assert.True(sizes[0].MeasuredBytes >= 5_000_000);
        Assert.True(sizes[1].IsMeasured);
        Assert.True(sizes[1].MeasuredBytes < 999_999_999, "gemessene Größe hat Vorrang vor der Schätzung des Installers");
        Assert.False(sizes[2].IsMeasured);
        Assert.Equal(1234, sizes[2].DisplayBytes);
        Assert.Null(sizes[3].DisplayBytes);
    }

    [Fact]
    public void Reading_the_installed_programs_works_on_this_machine_and_lists_named_entries_only()
    {
        IReadOnlyList<InstalledProgram> programs = InstalledProgramsProbe.Read();

        Assert.All(programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.All(programs.Where(p => p.InstallLocation is not null), p => Assert.True(Directory.Exists(p.InstallLocation), p.InstallLocation));
    }
}
