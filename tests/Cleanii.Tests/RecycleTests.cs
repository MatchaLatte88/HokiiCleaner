using System.Text;
using Cleanii.Actions;

namespace Cleanii.Tests;

/// <summary>Alle Tests arbeiten in Testordnern unter %TEMP%; der echte Papierkorb wird nie berührt (der Recycler ist ein Fake).</summary>
public class RecycleTests
{
    private sealed class FakePolicy : IBinPolicy
    {
        public Func<string, BinDriveInfo> Describe { get; init; } = _ => new BinDriveInfo(null, null);

        BinDriveInfo IBinPolicy.Describe(string path) => Describe(path);
    }

    /// <summary>Verschiebt in einen Ordner "fakebin" im Testbaum.</summary>
    private sealed class FakeRecycler : IRecycler
    {
        private readonly string _bin;
        private int _counter;

        public FakeRecycler(string bin) => _bin = Directory.CreateDirectory(bin).FullName;

        public List<string> Calls { get; } = new();

        public Func<string, RecycleOutcome?>? Override { get; init; }

        public RecycleOutcome Recycle(string path, bool isDirectory)
        {
            Calls.Add(path);
            if (Override?.Invoke(path) is { } custom)
            {
                return custom;
            }

            string target = Path.Combine(_bin, "$R" + _counter++);
            if (isDirectory)
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            return RecycleOutcome.Done;
        }
    }

    private static PathGuard GuardFor(TempTree tree)
        => new(new ProtectedLocations(
            Trees: new[] { tree.Combine("Windows"), tree.Combine("Program Files") },
            Exact: new[] { tree.Combine("Users", "me"), tree.Combine("Users", "me", "Documents") },
            DriveRootNames: new[] { "pagefile.sys", "$Recycle.Bin" }));

    private static RecycleCandidate Candidate(string path, bool directory = false, long size = 1_000, long files = 1)
        => new(path, directory, size, files);

    // ------------------------------------------------------------ Schutzliste

    [Fact]
    public void Guard_denies_protected_trees_and_everything_below_them()
    {
        using var tree = new TempTree();
        PathGuard guard = GuardFor(tree);

        Assert.False(guard.Check(tree.Combine("Windows"), false).Allowed);
        Assert.False(guard.Check(tree.Combine("Windows", "System32", "x.dll"), false).Allowed);
        Assert.False(guard.Check(tree.Combine("Program Files", "App", "app.exe"), false).Allowed);
        Assert.True(guard.Check(tree.Combine("Program Files Extras", "x.bin"), false).Allowed); // gleicher Anfang, anderer Ordner
    }

    [Fact]
    public void Guard_denies_a_folder_that_contains_protected_locations()
    {
        using var tree = new TempTree();
        PathGuard guard = GuardFor(tree);

        GuardVerdict verdict = guard.Check(tree.Root, false);

        Assert.False(verdict.Allowed);
        Assert.Contains("Enthält", verdict.Reason);
        Assert.False(guard.Check(tree.Combine("Users"), false).Allowed); // enthält das Profil
    }

    [Fact]
    public void Guard_protects_profile_folders_themselves_but_not_their_contents()
    {
        using var tree = new TempTree();
        PathGuard guard = GuardFor(tree);

        Assert.False(guard.Check(tree.Combine("Users", "me"), false).Allowed);
        Assert.False(guard.Check(tree.Combine("Users", "me", "Documents"), false).Allowed);
        Assert.True(guard.Check(tree.Combine("Users", "me", "Documents", "old.docx"), false).Allowed);
        Assert.True(guard.Check(tree.Combine("Users", "me", "Videos"), false).Allowed);
    }

    [Fact]
    public void Guard_denies_drive_roots_system_files_on_the_root_relative_paths_and_network_paths()
    {
        using var tree = new TempTree();
        PathGuard guard = GuardFor(tree);
        string drive = Path.GetPathRoot(tree.Root)!;

        Assert.False(guard.Check(drive, false).Allowed);
        Assert.False(guard.Check(Path.Combine(drive, "pagefile.sys"), false).Allowed);
        Assert.False(guard.Check(Path.Combine(drive, "$Recycle.Bin"), false).Allowed);
        Assert.False(guard.Check(@"relativ\pfad.txt", false).Allowed);
        Assert.False(guard.Check(@"\\server\share\datei.bin", false).Allowed);
        Assert.False(guard.Check(string.Empty, false).Allowed);
    }

    [Fact]
    public void Guard_user_trees_are_added()
    {
        using var tree = new TempTree();
        var guard = new PathGuard(new ProtectedLocations(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
            .WithTrees(new[] { tree.Combine("geschuetzt") }));

        Assert.False(guard.Check(tree.Combine("geschuetzt", "x.bin"), false).Allowed);
        Assert.True(guard.Check(tree.Combine("frei", "x.bin"), false).Allowed);
    }

    [Fact]
    public void Guard_probe_rejects_missing_entries_and_junctions()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"echt\f.bin", 10);
        tree.CreateJunction("link", "echt");
        PathGuard guard = GuardFor(tree);

        Assert.True(guard.Check(tree.Combine("echt"), true).Allowed);
        Assert.False(guard.Check(tree.Combine("fehlt.bin"), true).Allowed);
        GuardVerdict link = guard.Check(tree.Combine("link"), true);
        Assert.False(link.Allowed);
        Assert.Contains("Verknüpfung", link.Reason);
    }

    // ------------------------------------------------------------ Plan

    [Fact]
    public void Plan_accepts_allowed_entries_and_refuses_protected_ones_with_a_reason()
    {
        using var tree = new TempTree();
        string fine = tree.CreateFile(@"daten\gross.bin", 100);
        string protectedFile = tree.CreateFile(@"Windows\system.dll", 100);

        RecyclePlan plan = RecycleEngine.Plan(
            new[] { Candidate(fine, size: 5_000), Candidate(protectedFile, size: 9_000) }, GuardFor(tree), new FakePolicy());

        RecycleCandidate accepted = Assert.Single(plan.Accepted);
        Assert.Equal(fine, accepted.Path);
        RecycleRefusal refusal = Assert.Single(plan.Refused);
        Assert.Equal(protectedFile, refusal.Item.Path);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Reason));
        Assert.Equal(5_000, plan.AcceptedBytes);
    }

    [Fact]
    public void Plan_drops_entries_inside_a_selected_folder_and_says_so()
    {
        using var tree = new TempTree();
        string folder = tree.CreateDirectory("proj");
        string inner = tree.CreateFile(@"proj\sub\a.bin", 100);
        string outer = tree.CreateFile(@"anders\b.bin", 100);

        RecyclePlan plan = RecycleEngine.Plan(
            new[] { Candidate(inner), Candidate(folder, directory: true, size: 9_000, files: 4), Candidate(outer) },
            GuardFor(tree), new FakePolicy());

        Assert.Equal(2, plan.Accepted.Count);
        Assert.DoesNotContain(plan.Accepted, a => a.Path == inner);
        Assert.Contains(plan.Notes, n => n.Contains("ebenfalls gewählten Ordner"));
    }

    [Fact]
    public void Plan_ignores_duplicate_paths_regardless_of_case()
    {
        using var tree = new TempTree();
        string file = tree.CreateFile("a.bin", 100);

        RecyclePlan plan = RecycleEngine.Plan(
            new[] { Candidate(file), Candidate(file.ToUpperInvariant()) }, GuardFor(tree), new FakePolicy());

        Assert.Single(plan.Accepted);
    }

    [Fact]
    public void Plan_refuses_entries_that_the_recycle_bin_could_not_hold()
    {
        using var tree = new TempTree();
        string huge = tree.CreateFile("riesig.bin", 100);
        string small = tree.CreateFile("klein.bin", 100);
        var policy = new FakePolicy { Describe = _ => new BinDriveInfo(null, MaxBytes: 10_000) };

        RecyclePlan plan = RecycleEngine.Plan(
            new[] { Candidate(huge, size: 10_000), Candidate(small, size: 9_999) }, GuardFor(tree), policy);

        Assert.Equal(small, Assert.Single(plan.Accepted).Path);
        RecycleRefusal refusal = Assert.Single(plan.Refused);
        Assert.Equal(huge, refusal.Item.Path);
        Assert.Contains("endgültig", refusal.Reason);
    }

    [Fact]
    public void Plan_refuses_everything_on_a_drive_without_a_recycle_bin()
    {
        using var tree = new TempTree();
        string file = tree.CreateFile("a.bin", 100);
        var policy = new FakePolicy { Describe = _ => new BinDriveInfo("Kein Papierkorb auf diesem Laufwerk.", null) };

        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(file) }, GuardFor(tree), policy);

        Assert.Empty(plan.Accepted);
        Assert.Equal("Kein Papierkorb auf diesem Laufwerk.", Assert.Single(plan.Refused).Reason);
    }

    [Fact]
    public void Plan_handles_an_empty_selection()
    {
        using var tree = new TempTree();

        RecyclePlan plan = RecycleEngine.Plan(Array.Empty<RecycleCandidate>(), GuardFor(tree), new FakePolicy());

        Assert.Empty(plan.Accepted);
        Assert.Empty(plan.Refused);
        Assert.Equal(0, plan.AcceptedBytes);
    }

    // ------------------------------------------------------------ Run

    [Fact]
    public void Run_moves_files_and_folders_and_reports_them()
    {
        using var tree = new TempTree();
        string file = tree.CreateFile(@"daten\a.bin", 100);
        string folder = tree.CreateFile(@"proj\x\b.bin", 100);
        string proj = tree.Combine("proj");
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(
            new[] { Candidate(file, size: 4_096), Candidate(proj, directory: true, size: 8_192, files: 1) }, guard, new FakePolicy());
        var recycler = new FakeRecycler(tree.Combine("fakebin"));

        RecycleResult result = RecycleEngine.Run(plan, guard, recycler, null, CancellationToken.None);

        Assert.Equal(2, result.MovedCount);
        Assert.Equal(12_288, result.MovedBytes);
        Assert.False(result.Cancelled);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(proj));
        Assert.True(File.Exists(Path.Combine(tree.Combine("fakebin"), "$R0")) || Directory.Exists(Path.Combine(tree.Combine("fakebin"), "$R0")));
        _ = folder;
    }

    [Fact]
    public void Run_continues_after_a_failure_and_reports_it()
    {
        using var tree = new TempTree();
        string locked = tree.CreateFile("gesperrt.bin", 100);
        string fine = tree.CreateFile("frei.bin", 100);
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(locked), Candidate(fine) }, guard, new FakePolicy());
        var recycler = new FakeRecycler(tree.Combine("fakebin"))
        {
            Override = p => p == locked ? new RecycleOutcome(false, "in Benutzung") : null,
        };

        RecycleResult result = RecycleEngine.Run(plan, guard, recycler, null, CancellationToken.None);

        Assert.Equal(1, result.MovedCount);
        RecycleItemResult failure = Assert.Single(result.Items, i => i.Status == RecycleStatus.Failed);
        Assert.Equal(locked, failure.Item.Path);
        Assert.Equal("in Benutzung", failure.Detail);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(fine));
        Assert.Equal(1, result.ProblemCount);
    }

    [Fact]
    public void Run_turns_exceptions_of_the_recycler_into_failures()
    {
        using var tree = new TempTree();
        string file = tree.CreateFile("a.bin", 100);
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(file) }, guard, new FakePolicy());
        var throwing = new ThrowingRecycler();

        RecycleResult result = RecycleEngine.Run(plan, guard, throwing, null, CancellationToken.None);

        RecycleItemResult item = Assert.Single(result.Items);
        Assert.Equal(RecycleStatus.Failed, item.Status);
        Assert.Contains("kaputt", item.Detail);
    }

    private sealed class ThrowingRecycler : IRecycler
    {
        public RecycleOutcome Recycle(string path, bool isDirectory) => throw new IOException("kaputt");
    }

    [Fact]
    public void Run_does_not_trust_a_success_that_left_the_entry_in_place()
    {
        using var tree = new TempTree();
        string file = tree.CreateFile("a.bin", 100);
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(file) }, guard, new FakePolicy());
        var recycler = new FakeRecycler(tree.Combine("fakebin")) { Override = _ => RecycleOutcome.Done };

        RecycleResult result = RecycleEngine.Run(plan, guard, recycler, null, CancellationToken.None);

        RecycleItemResult item = Assert.Single(result.Items);
        Assert.Equal(RecycleStatus.Failed, item.Status);
        Assert.Contains("noch da", item.Detail);
    }

    [Fact]
    public void Run_checks_again_right_before_moving_and_refuses_a_replaced_entry()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"ziel\f.bin", 10);
        string folder = tree.CreateDirectory("ordner");
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(folder, directory: true) }, guard, new FakePolicy());

        // Nach der Planung wird der Ordner durch eine Junction ersetzt.
        Directory.Delete(folder);
        tree.CreateJunction("ordner", "ziel");
        var recycler = new FakeRecycler(tree.Combine("fakebin"));

        RecycleResult result = RecycleEngine.Run(plan, guard, recycler, null, CancellationToken.None);

        Assert.Equal(RecycleStatus.Refused, Assert.Single(result.Items).Status);
        Assert.Empty(recycler.Calls);
        Assert.True(File.Exists(tree.Combine("ziel", "f.bin")));
    }

    [Fact]
    public void Run_stops_after_cancellation_and_marks_the_rest_as_not_attempted()
    {
        using var tree = new TempTree();
        string a = tree.CreateFile("a.bin", 100);
        string b = tree.CreateFile("b.bin", 100);
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(a), Candidate(b) }, guard, new FakePolicy());
        using var cts = new CancellationTokenSource();
        var recycler = new FakeRecycler(tree.Combine("fakebin"))
        {
            Override = _ =>
            {
                cts.Cancel(); // nach dem ersten Eintrag abbrechen
                return null;
            },
        };

        RecycleResult result = RecycleEngine.Run(plan, guard, recycler, null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(1, result.MovedCount);
        Assert.Single(result.Items, i => i.Status == RecycleStatus.NotAttempted);
    }

    [Fact]
    public void Run_flags_a_bin_that_grew_less_than_the_number_of_moved_entries()
    {
        using var tree = new TempTree();
        string a = tree.CreateFile("a.bin", 100);
        string b = tree.CreateFile("b.bin", 100);
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(a), Candidate(b) }, guard, new FakePolicy());
        int call = 0;

        RecycleResult result = RecycleEngine.Run(
            plan, guard, new FakeRecycler(tree.Combine("fakebin")), null, CancellationToken.None,
            _ => call++ == 0 ? 10 : 11); // zwei verschoben, aber nur einer mehr im Papierkorb

        Assert.True(result.BinGrowthLooksShort);
    }

    [Fact]
    public void Run_without_a_bin_counter_does_not_claim_anything_about_the_bin()
    {
        using var tree = new TempTree();
        string a = tree.CreateFile("a.bin", 100);
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(a) }, guard, new FakePolicy());

        RecycleResult result = RecycleEngine.Run(plan, guard, new FakeRecycler(tree.Combine("fakebin")), null, CancellationToken.None);

        Assert.False(result.BinGrowthLooksShort);
        Assert.Null(result.BinItemsBefore);
    }

    // ------------------------------------------------------------ Protokoll

    [Fact]
    public void Log_lists_every_moved_path_and_everything_that_was_not_moved()
    {
        using var tree = new TempTree();
        string moved = tree.CreateFile("verschoben.bin", 100);
        string locked = tree.CreateFile("gesperrt.bin", 100);
        string protectedFile = tree.CreateFile(@"Windows\x.dll", 100);
        PathGuard guard = GuardFor(tree);
        RecyclePlan plan = RecycleEngine.Plan(new[] { Candidate(moved), Candidate(locked), Candidate(protectedFile) }, guard, new FakePolicy());
        var recycler = new FakeRecycler(tree.Combine("fakebin"))
        {
            Override = p => p == locked ? new RecycleOutcome(false, "in Benutzung") : null,
        };
        RecycleResult result = RecycleEngine.Run(plan, guard, recycler, null, CancellationToken.None);

        string path = RecycleLog.Write(tree.Combine("logs"), result, plan);
        string text = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains(moved, text);
        Assert.Contains(locked, text);
        Assert.Contains("in Benutzung", text);
        Assert.Contains(protectedFile, text);
        Assert.Contains("[Nicht verschoben]", text);
    }

    // ------------------------------------------------------------ Papierkorb lesen

    private static byte[] MetaV2(string original, long size, DateTime deletedUtc)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(2L);
        writer.Write(size);
        writer.Write(deletedUtc.ToFileTimeUtc());
        writer.Write(original.Length + 1);
        writer.Write(Encoding.Unicode.GetBytes(original + "\0"));
        return stream.ToArray();
    }

    private static byte[] MetaV1(string original, long size, DateTime deletedUtc)
    {
        var data = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(data, 0);
        BitConverter.GetBytes(size).CopyTo(data, 8);
        BitConverter.GetBytes(deletedUtc.ToFileTimeUtc()).CopyTo(data, 16);
        Encoding.Unicode.GetBytes(original).CopyTo(data, 24);
        return data;
    }

    [Fact]
    public void Metadata_v2_is_parsed()
    {
        var deleted = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        bool ok = RecycleBinMetadata.TryParse(MetaV2(@"C:\Users\me\Doku ment.txt", 1234, deleted), out string path, out DateTime when, out long size);

        Assert.True(ok);
        Assert.Equal(@"C:\Users\me\Doku ment.txt", path);
        Assert.Equal(deleted, when);
        Assert.Equal(1234, size);
    }

    [Fact]
    public void Metadata_v1_is_parsed()
    {
        var deleted = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        bool ok = RecycleBinMetadata.TryParse(MetaV1(@"D:\alt\datei.bin", 99, deleted), out string path, out DateTime when, out long size);

        Assert.True(ok);
        Assert.Equal(@"D:\alt\datei.bin", path);
        Assert.Equal(deleted, when);
        Assert.Equal(99, size);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(27)]
    public void Metadata_that_is_too_short_is_rejected(int length)
    {
        Assert.False(RecycleBinMetadata.TryParse(new byte[length], out _, out _, out _));
    }

    [Fact]
    public void Metadata_with_an_unknown_version_or_a_cut_off_path_is_rejected()
    {
        byte[] unknown = MetaV2(@"C:\x.txt", 1, DateTime.UtcNow);
        BitConverter.GetBytes(7L).CopyTo(unknown, 0);
        byte[] cut = MetaV2(@"C:\sehr\langer\pfad.txt", 1, DateTime.UtcNow);

        Assert.False(RecycleBinMetadata.TryParse(unknown, out _, out _, out _));
        Assert.False(RecycleBinMetadata.TryParse(cut.AsSpan(0, cut.Length - 10), out _, out _, out _));
    }

    private static string FakeUserFolder(TempTree tree, string id, string original, byte[] content, bool directory)
    {
        string folder = tree.CreateDirectory(@"bin\sid");
        File.WriteAllBytes(Path.Combine(folder, "$I" + id), MetaV2(original, content.Length, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        string data = Path.Combine(folder, "$R" + id);
        if (directory)
        {
            Directory.CreateDirectory(data);
            File.WriteAllBytes(Path.Combine(data, "inner.bin"), content);
        }
        else
        {
            File.WriteAllBytes(data, content);
        }

        return folder;
    }

    [Fact]
    public void List_reads_entries_with_original_path_and_kind()
    {
        using var tree = new TempTree();
        string drive = Path.GetPathRoot(tree.Root)!;
        string original = tree.Combine("urspruenglich", "doku.txt");
        string folder = FakeUserFolder(tree, "ABC123.txt", original, new byte[40], directory: false);
        FakeUserFolder(tree, "XYZ789", tree.Combine("urspruenglich", "ordner"), new byte[10], directory: true);

        BinListing listing = RecycleBin.List(drive, folder);

        Assert.Empty(listing.Problems);
        Assert.Equal(2, listing.Entries.Count);
        BinEntry file = listing.Entries.Single(e => !e.IsDirectory);
        Assert.Equal(original, file.OriginalPath);
        Assert.Equal(40, file.SizeBytes);
        Assert.Equal("doku.txt", file.Name);
        BinEntry dir = listing.Entries.Single(e => e.IsDirectory);
        Assert.Null(dir.SizeBytes);
        Assert.Equal((10L, 0), RecycleBin.MeasureDirectory(dir.DataPath, CancellationToken.None));
    }

    [Fact]
    public void List_reports_broken_entries_instead_of_hiding_them()
    {
        using var tree = new TempTree();
        string drive = Path.GetPathRoot(tree.Root)!;
        string folder = tree.CreateDirectory(@"bin\sid");
        File.WriteAllBytes(Path.Combine(folder, "$IKAPUTT"), new byte[5]);
        File.WriteAllBytes(Path.Combine(folder, "$IOHNEDATEN"), MetaV2(@"C:\weg.txt", 1, DateTime.UtcNow));

        BinListing listing = RecycleBin.List(drive, folder);

        Assert.Empty(listing.Entries);
        Assert.Contains("Format nicht erkannt", Assert.Single(listing.Problems));
        Assert.Equal(1, listing.Orphans); // Eintrag ohne Inhalt: Rest, den Windows selbst nicht anzeigt
    }

    [Fact]
    public void List_of_a_drive_without_a_user_folder_is_simply_empty()
    {
        using var tree = new TempTree();

        BinListing listing = RecycleBin.List(Path.GetPathRoot(tree.Root)!, tree.Combine("gibt-es-nicht"));

        Assert.Empty(listing.Entries);
        Assert.Empty(listing.Problems);
    }

    [Fact]
    public void Restore_moves_the_entry_back_and_removes_its_record()
    {
        using var tree = new TempTree();
        string drive = Path.GetPathRoot(tree.Root)!;
        string original = tree.Combine("alt", "tief", "doku.txt");
        string folder = FakeUserFolder(tree, "ABC123.txt", original, new byte[40], directory: false);
        BinEntry entry = RecycleBin.List(drive, folder).Entries.Single();

        RestoreOutcome outcome = RecycleBin.Restore(entry);

        Assert.Equal(RestoreStatus.Restored, outcome.Status);
        Assert.Equal(40, new FileInfo(original).Length);
        Assert.False(File.Exists(entry.DataPath));
        Assert.False(File.Exists(entry.MetaPath));
    }

    [Fact]
    public void Restore_never_overwrites_something_that_is_there_now()
    {
        using var tree = new TempTree();
        string drive = Path.GetPathRoot(tree.Root)!;
        string original = tree.CreateFile(@"alt\doku.txt", 7);
        string folder = FakeUserFolder(tree, "ABC123.txt", original, new byte[40], directory: false);
        BinEntry entry = RecycleBin.List(drive, folder).Entries.Single();

        RestoreOutcome outcome = RecycleBin.Restore(entry);

        Assert.Equal(RestoreStatus.TargetExists, outcome.Status);
        Assert.Equal(7, new FileInfo(original).Length);
        Assert.True(File.Exists(entry.DataPath));
    }

    [Fact]
    public void Restore_of_a_folder_brings_back_its_contents()
    {
        using var tree = new TempTree();
        string drive = Path.GetPathRoot(tree.Root)!;
        string original = tree.Combine("proj");
        string folder = FakeUserFolder(tree, "DIR001", original, new byte[10], directory: true);
        BinEntry entry = RecycleBin.List(drive, folder).Entries.Single();

        Assert.Equal(RestoreStatus.Restored, RecycleBin.Restore(entry).Status);
        Assert.True(File.Exists(Path.Combine(original, "inner.bin")));
    }
}
