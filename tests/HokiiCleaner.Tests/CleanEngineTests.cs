using HokiiCleaner.Actions;

namespace HokiiCleaner.Tests;

/// <summary>Alle Tests löschen nur in Testordnern unter %TEMP%\HokiiCleaner.Tests.*; echte Ziele (Temp, Caches) werden nie berührt.</summary>
public class CleanEngineTests
{
    private static CleanTarget Target(string root, TimeSpan? minAge = null, string[]? patterns = null, string[]? names = null)
        => new("test", "Test", "d", "c", CleanTier.Cache, new[] { root }, minAge ?? TimeSpan.Zero, false, names, patterns);

    private static void SetAge(string path, int days)
    {
        DateTime time = DateTime.UtcNow.AddDays(-days);
        if (Directory.Exists(path))
        {
            Directory.SetCreationTimeUtc(path, time);
            Directory.SetLastWriteTimeUtc(path, time);
            Directory.SetLastAccessTimeUtc(path, time);
        }
        else
        {
            File.SetCreationTimeUtc(path, time);
            File.SetLastWriteTimeUtc(path, time);
            File.SetLastAccessTimeUtc(path, time);
        }
    }

    private static TargetRun Clean(CleanTarget target) => CleanEngine.Clean(target, DateTime.UtcNow, null, CancellationToken.None);

    [Fact]
    public void Analyze_counts_what_would_be_deleted_and_changes_nothing()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"cache\a.bin", 1000);
        tree.CreateFile(@"cache\sub\b.bin", 3000);
        tree.CreateFile(@"cache\sub\deeper\c.bin", 2000);

        TargetRun run = CleanEngine.Analyze(Target(tree.Combine("cache")), DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(3, run.MatchedFiles);
        Assert.Equal(6000, run.MatchedBytes);
        Assert.Equal(0, run.DeletedFiles);
        Assert.Equal(new[] { 3000L, 2000L, 1000L }, run.Largest.Select(f => f.Length));
        Assert.True(File.Exists(tree.Combine(@"cache\sub\deeper\c.bin")));
    }

    [Fact]
    public void Clean_deletes_all_files_removes_empty_subfolders_and_keeps_the_root()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"cache\a.bin", 1000);
        tree.CreateFile(@"cache\sub\deeper\c.bin", 2000);
        tree.CreateDirectory(@"cache\empty");

        TargetRun run = Clean(Target(tree.Combine("cache")));

        Assert.Equal(2, run.DeletedFiles);
        Assert.Equal(3000, run.DeletedBytes);
        Assert.Equal(3, run.DirectoriesRemoved); // sub\deeper, sub, empty
        Assert.Equal(0, run.SkippedTotal);
        Assert.True(Directory.Exists(tree.Combine("cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(tree.Combine("cache")));
    }

    [Fact]
    public void Analysis_and_cleaning_agree_on_what_matches()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"cache\old.bin", 500);
        tree.CreateFile(@"cache\sub\young.bin", 700);
        SetAge(tree.Combine(@"cache\old.bin"), 10);
        CleanTarget target = Target(tree.Combine("cache"), TimeSpan.FromDays(7));

        TargetRun analysis = CleanEngine.Analyze(target, DateTime.UtcNow, CancellationToken.None);
        TargetRun cleaned = Clean(target);

        Assert.Equal(analysis.MatchedFiles, cleaned.DeletedFiles);
        Assert.Equal(analysis.MatchedBytes, cleaned.DeletedBytes);
    }

    [Fact]
    public void Minimum_age_keeps_young_files_and_folders_that_still_hold_something()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"temp\old.txt", 100);
        tree.CreateFile(@"temp\keep\young.txt", 100);
        tree.CreateFile(@"temp\gone2\deep\old.bin", 100);
        tree.CreateDirectory(@"temp\gone");
        tree.CreateDirectory(@"temp\youngdir");

        // Zuletzt altern lassen (Dateien vor Ordnern), weil Inhalte die Zeitstempel ihres Ordners verändern.
        SetAge(tree.Combine(@"temp\old.txt"), 10);
        SetAge(tree.Combine(@"temp\gone2\deep\old.bin"), 10);
        foreach (string dir in new[] { @"temp\gone2\deep", @"temp\gone2", @"temp\gone", @"temp\keep" })
        {
            SetAge(tree.Combine(dir), 10);
        }

        TargetRun run = Clean(Target(tree.Combine("temp"), TimeSpan.FromDays(7)));

        Assert.Equal(2, run.DeletedFiles);
        Assert.Equal(3, run.DirectoriesRemoved); // gone, gone2\deep, gone2
        Assert.True(File.Exists(tree.Combine(@"temp\keep\young.txt")));
        Assert.True(Directory.Exists(tree.Combine(@"temp\keep")));
        Assert.True(Directory.Exists(tree.Combine(@"temp\youngdir")));
        Assert.False(Directory.Exists(tree.Combine(@"temp\gone2")));
    }

    [Fact]
    public void A_recently_created_file_is_kept_even_when_its_modified_time_is_old()
    {
        using var tree = new TempTree();
        string path = tree.CreateFile(@"temp\extracted.dll", 100);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30)); // z. B. aus einem Archiv mit altem Datum entpackt

        TargetRun run = Clean(Target(tree.Combine("temp"), TimeSpan.FromDays(7)));

        Assert.Equal(0, run.DeletedFiles);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Junctions_are_neither_followed_nor_removed()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"cache\a.bin", 100);
        string outside = tree.CreateFile(@"outside\precious.docx", 100);
        tree.CreateJunction(@"cache\link", "outside");

        TargetRun run = Clean(Target(tree.Combine("cache")));

        Assert.Equal(1, run.DeletedFiles);
        Assert.Equal(1, run.LinksSkipped);
        Assert.True(File.Exists(outside));
        Assert.True(Directory.Exists(tree.Combine(@"cache\link")));
    }

    [Fact]
    public void File_patterns_only_match_files_directly_in_the_root()
    {
        using var tree = new TempTree();
        string thumb = tree.CreateFile(@"explorer\thumbcache_96.db", 100);
        string icon = tree.CreateFile(@"explorer\iconcache_32.DB", 100);
        string other = tree.CreateFile(@"explorer\settings.dat", 100);
        string nested = tree.CreateFile(@"explorer\sub\thumbcache_nested.db", 100);

        TargetRun run = Clean(Target(tree.Combine("explorer"), patterns: new[] { "thumbcache_*.db", "iconcache_*.db" }));

        Assert.Equal(2, run.DeletedFiles);
        Assert.False(File.Exists(thumb));
        Assert.False(File.Exists(icon));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void A_file_in_use_is_skipped_reported_and_does_not_stop_the_rest()
    {
        using var tree = new TempTree();
        string locked = tree.CreateFile(@"cache\locked.bin", 100);
        string free = tree.CreateFile(@"cache\free.bin", 100);

        TargetRun run;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            run = Clean(Target(tree.Combine("cache")));
        }

        Assert.Equal(1, run.DeletedFiles);
        Assert.Equal(1, run.SkippedCounts[SkipReason.InUse]);
        Assert.Contains(run.SkippedSample, s => s.Path == locked && s.Reason == SkipReason.InUse);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void Read_only_files_are_deleted()
    {
        using var tree = new TempTree();
        string path = tree.CreateFile(@"cache\readonly.bin", 100);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        TargetRun run = Clean(Target(tree.Combine("cache")));

        Assert.Equal(1, run.DeletedFiles);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void An_unreadable_folder_is_reported_and_its_siblings_are_still_cleaned()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"cache\a.bin", 100);
        tree.CreateFile(@"cache\secret\hidden.bin", 100);
        tree.DenyListing(tree.Combine(@"cache\secret"));

        TargetRun run = Clean(Target(tree.Combine("cache")));

        Assert.Equal(1, run.DeletedFiles);
        Assert.Equal(1, run.SkippedCounts[SkipReason.UnreadableFolder]);
    }

    [Fact]
    public void A_cancelled_run_stops_without_touching_anything()
    {
        using var tree = new TempTree();
        string path = tree.CreateFile(@"cache\a.bin", 100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        TargetRun run = CleanEngine.Clean(Target(tree.Combine("cache")), DateTime.UtcNow, null, cts.Token);

        Assert.True(run.Cancelled);
        Assert.Equal(0, run.DeletedFiles);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_missing_root_is_reported_as_missing_not_as_a_problem()
    {
        using var tree = new TempTree();

        TargetRun run = Clean(Target(tree.Combine("does-not-exist")));

        Assert.True(run.Missing);
        Assert.Null(run.Problem);
        Assert.Equal(0, run.DeletedFiles);
    }

    [Fact]
    public void A_root_with_an_unexpected_name_is_rejected_and_nothing_is_deleted()
    {
        using var tree = new TempTree();
        string path = tree.CreateFile(@"Documents\thesis.docx", 100);

        TargetRun run = Clean(Target(tree.Combine("Documents"), names: new[] { "Temp", "Tmp" }));

        Assert.NotNull(run.Problem);
        Assert.False(run.Missing);
        Assert.Equal(0, run.DeletedFiles);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_root_that_is_a_junction_is_rejected()
    {
        using var tree = new TempTree();
        string precious = tree.CreateFile(@"real\precious.docx", 100);
        tree.CreateJunction("temp", "real");

        TargetRun run = Clean(Target(tree.Combine("temp")));

        Assert.NotNull(run.Problem);
        Assert.Equal(0, run.DeletedFiles);
        Assert.True(File.Exists(precious));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"relative\path")]
    [InlineData("")]
    public void Roots_too_close_to_the_drive_root_or_not_fully_qualified_are_rejected(string root)
    {
        (RootState state, string? reason) = RootGuard.Check(root, null);

        Assert.Equal(RootState.Rejected, state);
        Assert.NotNull(reason);
    }

    [Fact]
    public void IsInside_requires_a_real_child_and_normalizes_the_path()
    {
        string root = @"C:\Users\x\AppData\Local\Temp";

        Assert.True(RootGuard.IsInside(root, @"C:\Users\x\AppData\Local\Temp\a\b.tmp"));
        Assert.True(RootGuard.IsInside(root, @"c:\users\X\appdata\local\temp\a.tmp"));
        Assert.False(RootGuard.IsInside(root, root));
        Assert.False(RootGuard.IsInside(root, @"C:\Users\x\AppData\Local\Temp2\a.tmp"));
        Assert.False(RootGuard.IsInside(root, @"C:\Users\x\AppData\Local\Temp\..\Documents\a.docx"));
    }

    [Fact]
    public void CleanAll_sums_up_and_measures_free_space()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"one\a.bin", 1000);
        tree.CreateFile(@"two\b.bin", 2000);
        var targets = new[] { Target(tree.Combine("one")), Target(tree.Combine("two")) };

        CleanResult result = CleanEngine.CleanAll(targets, null, CancellationToken.None);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(3000, result.DeletedBytes);
        Assert.NotNull(result.FreeBytesBefore);
        Assert.NotNull(result.FreeBytesAfter);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public void The_log_lists_deleted_totals_and_every_skipped_file()
    {
        using var tree = new TempTree();
        string locked = tree.CreateFile(@"cache\locked.bin", 100);
        tree.CreateFile(@"cache\free.bin", 100);

        CleanResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = CleanEngine.CleanAll(new[] { Target(tree.Combine("cache")) }, null, CancellationToken.None);
        }

        string logPath = CleanLog.Write(tree.Combine("logs"), result);
        string text = File.ReadAllText(logPath);

        Assert.Contains("Gelöscht: 1 Dateien", text);
        Assert.Contains("Übersprungen (InUse): 1", text);
        Assert.Contains(locked, text);
    }

    [Fact]
    public void An_excluded_folder_is_not_counted_entered_or_removed_even_when_old()
    {
        using var tree = new TempTree();
        string kept = tree.CreateFile(@"temp\claude\session\scratch.txt", 100);
        string keptEmpty = tree.CreateDirectory(@"temp\claude\emptydir");
        string sibling = tree.CreateFile(@"temp\other.tmp", 100);
        string nested = tree.CreateFile(@"temp\sub\claude\file.tmp", 100); // gleicher Name tiefer: nicht ausgenommen
        foreach (string path in new[] { kept, sibling, nested })
        {
            SetAge(path, 30);
        }

        foreach (string dir in new[] { @"temp\claude\session", @"temp\claude\emptydir", @"temp\claude" })
        {
            SetAge(tree.Combine(dir), 30);
        }

        CleanTarget target = Target(tree.Combine("temp"), TimeSpan.FromDays(7)) with { ExcludedFolders = new[] { "claude" } };

        TargetRun analysis = CleanEngine.Analyze(target, DateTime.UtcNow, CancellationToken.None);
        TargetRun run = Clean(target);

        Assert.Equal(2, analysis.MatchedFiles); // other.tmp und sub\claude\file.tmp
        Assert.Equal(2, run.DeletedFiles);
        Assert.True(File.Exists(kept));
        Assert.True(Directory.Exists(keptEmpty));
        Assert.False(File.Exists(sibling));
        Assert.False(File.Exists(nested));
    }

    [Fact]
    public void The_user_temp_target_excludes_the_claude_working_folder()
    {
        using var tree = new TempTree();
        var locations = new CleanLocations(tree.Combine("Temp"), tree.Combine(@"Windows\Temp"), tree.Combine("Local"));

        CleanTarget userTemp = CleanTargets.Build(locations).Single(t => t.Id == "user-temp");

        Assert.Contains("claude", userTemp.ExcludedFolders!);
    }

    [Fact]
    public void Every_target_root_stays_below_the_given_locations_and_ids_are_unique()
    {
        using var tree = new TempTree();
        var locations = new CleanLocations(
            tree.Combine("Temp"), tree.Combine(@"Windows\Temp"), tree.Combine("Local"), tree.Combine("Profile"), tree.Combine("Windows"));

        IReadOnlyList<CleanTarget> targets = CleanTargets.Build(locations);

        Assert.Equal(targets.Count, targets.Select(t => t.Id).Distinct().Count());
        foreach (CleanTarget target in targets)
        {
            Assert.All(target.Roots, root => Assert.StartsWith(tree.Root, root, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(target.AllowedRootNames!);
        }

        Assert.All(targets.Where(t => t.Tier == CleanTier.Temp), t => Assert.True(t.DefaultSelected && t.MinAge >= TimeSpan.FromDays(7)));
        Assert.All(targets.Where(t => t.Tier == CleanTier.Cache), t => Assert.False(t.DefaultSelected));
    }

    [Fact]
    public void Every_root_name_is_allowed_by_its_own_target()
    {
        using var tree = new TempTree();
        var locations = new CleanLocations(
            tree.Combine("Temp"), tree.Combine(@"Windows\Temp"), tree.Combine("Local"), tree.Combine("Profile"), tree.Combine("Windows"));

        foreach (CleanTarget target in CleanTargets.Build(locations))
        {
            foreach (string root in target.Roots)
            {
                (RootState state, string? reason) = RootGuard.Check(root, target.AllowedRootNames);
                Assert.True(state != RootState.Rejected, $"{target.Id}: {reason}");
            }
        }
    }

    [Fact]
    public void Targets_that_need_the_profile_or_windows_folder_are_left_out_without_them()
    {
        using var tree = new TempTree();
        var minimal = new CleanLocations(tree.Combine("Temp"), tree.Combine(@"Windows\Temp"), tree.Combine("Local"));

        string[] ids = CleanTargets.Build(minimal).Select(t => t.Id).ToArray();

        Assert.DoesNotContain("nuget-packages", ids);
        Assert.DoesNotContain("gradle-caches", ids);
        Assert.DoesNotContain("windows-update-download", ids);
        Assert.Contains("chrome-cache", ids);
    }

    [Fact]
    public void Browser_targets_cover_every_profile_but_only_the_cache_folders()
    {
        using var tree = new TempTree();
        string userData = @"Local\Google\Chrome\User Data";
        tree.CreateFile(userData + @"\Default\Cache\a.bin", 10);
        tree.CreateFile(userData + @"\Profile 1\Cache\b.bin", 10);
        tree.CreateFile(userData + @"\Profile 1\Cookies", 10);
        tree.CreateFile(userData + @"\Guest Profile\Cache\c.bin", 10);
        tree.CreateFile(userData + @"\System Profile\Cache\d.bin", 10);
        var locations = new CleanLocations(tree.Combine("Temp"), tree.Combine(@"Windows\Temp"), tree.Combine("Local"));

        CleanTarget chrome = CleanTargets.Build(locations).Single(t => t.Id == "chrome-cache");

        Assert.Contains(chrome.Roots, r => r.EndsWith(@"Default\Cache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(chrome.Roots, r => r.EndsWith(@"Profile 1\Code Cache", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(chrome.Roots, r => r.Contains("Guest Profile") || r.Contains("System Profile"));
        Assert.All(chrome.Roots, r => Assert.Contains(Path.GetFileName(r), new[] { "Cache", "Code Cache", "GPUCache" }));

        TargetRun run = CleanEngine.Analyze(chrome, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(2, run.MatchedFiles); // Cookies, Gast- und Systemprofil bleiben unberührt
    }

    [Fact]
    public void A_browser_that_is_not_installed_is_reported_as_missing()
    {
        using var tree = new TempTree();
        var locations = new CleanLocations(tree.Combine("Temp"), tree.Combine(@"Windows\Temp"), tree.Combine("Local"));

        TargetRun run = CleanEngine.Analyze(
            CleanTargets.Build(locations).Single(t => t.Id == "firefox-cache"), DateTime.UtcNow, CancellationToken.None);

        Assert.True(run.Missing);
    }
}
