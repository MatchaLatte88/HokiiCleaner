using Cleanii.Core.Dev;
using Cleanii.Core.History;
using Cleanii.Core.Scanning;

namespace Cleanii.Tests;

/// <summary>Tests für Entwickler-Funde und den gespeicherten Verlauf.</summary>
public class DevAndHistoryTests
{
    private static async Task<ScanResult> ScanAsync(TempTree tree) => await DiskScanner.ScanAsync(tree.Root);

    // ------------------------------------------------------------ Entwickler-Funde

    [Fact]
    public async Task Finds_node_modules_next_to_a_package_json_and_not_without_one()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"web\package.json", 10);
        tree.CreateFile(@"web\node_modules\left-pad\index.js", 5_000);
        tree.CreateFile(@"web\node_modules\left-pad\node_modules\inner\x.js", 5_000);
        tree.CreateFile(@"stray\node_modules\a.js", 5_000); // keine package.json daneben
        ScanResult scan = await ScanAsync(tree);

        IReadOnlyList<DevArtifact> found = DevArtifactFinder.Find(scan.Root, CancellationToken.None);

        DevArtifact artifact = Assert.Single(found);
        Assert.Equal(DevArtifactKind.NodeModules, artifact.Kind);
        Assert.Equal(tree.Combine("web", "node_modules"), artifact.FullPath);
        Assert.Equal(tree.Combine("web"), artifact.Project.FullPath);
        Assert.Equal(2, artifact.FileCount); // das verschachtelte node_modules zählt mit, wird aber nicht extra gefunden
    }

    [Fact]
    public async Task Finds_dotnet_build_folders_only_next_to_a_project_file()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"app\App.csproj", 10);
        tree.CreateFile(@"app\bin\Debug\app.dll", 9_000);
        tree.CreateFile(@"app\obj\project.assets.json", 9_000);
        tree.CreateFile(@"data\bin\x.bin", 9_000); // "bin" ohne Projektdatei
        ScanResult scan = await ScanAsync(tree);

        IReadOnlyList<DevArtifact> found = DevArtifactFinder.Find(scan.Root, CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.All(found, a => Assert.Equal(DevArtifactKind.DotNetBuild, a.Kind));
        Assert.DoesNotContain(found, a => a.FullPath.Contains("data"));
    }

    [Fact]
    public async Task Recognises_python_environments_by_pyvenv_cfg_and_warns_without_a_package_list()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"mit\.venv\pyvenv.cfg", 10);
        tree.CreateFile(@"mit\.venv\Lib\x.py", 9_000);
        tree.CreateFile(@"mit\requirements.txt", 10);
        tree.CreateFile(@"ohne\venv\pyvenv.cfg", 10);
        tree.CreateFile(@"ohne\venv\Lib\x.py", 9_000);
        tree.CreateFile(@"kein\venv\x.py", 9_000); // ohne pyvenv.cfg keine Umgebung
        ScanResult scan = await ScanAsync(tree);

        IReadOnlyList<DevArtifact> found = DevArtifactFinder.Find(scan.Root, CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.Null(found.Single(a => a.FullPath.Contains("mit")).Caution);
        Assert.NotNull(found.Single(a => a.FullPath.Contains("ohne")).Caution);
    }

    [Fact]
    public async Task Recognises_rust_maven_gradle_and_next_outputs()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"rust\Cargo.toml", 10);
        tree.CreateFile(@"rust\target\debug\a", 5_000);
        tree.CreateFile(@"maven\pom.xml", 10);
        tree.CreateFile(@"maven\target\classes\a", 5_000);
        tree.CreateFile(@"gradle\build.gradle.kts", 10);
        tree.CreateFile(@"gradle\build\libs\a.jar", 5_000);
        tree.CreateFile(@"next\package.json", 10);
        tree.CreateFile(@"next\.next\cache\a", 5_000);
        ScanResult scan = await ScanAsync(tree);

        DevArtifactKind[] kinds = DevArtifactFinder.Find(scan.Root, CancellationToken.None).Select(a => a.Kind).OrderBy(k => k).ToArray();

        Assert.Equal(
            new[] { DevArtifactKind.RustTarget, DevArtifactKind.MavenTarget, DevArtifactKind.GradleBuild, DevArtifactKind.NextBuild }.OrderBy(k => k).ToArray(),
            kinds);
    }

    [Fact]
    public async Task Project_activity_ignores_the_generated_folders()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"p\package.json", 10);
        tree.CreateFile(@"p\src\main.js", 10);
        tree.CreateFile(@"p\node_modules\x\i.js", 10);
        var sourceTime = new DateTime(2021, 5, 6, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(tree.Combine("p", "package.json"), sourceTime);
        File.SetLastWriteTimeUtc(tree.Combine("p", "src", "main.js"), sourceTime);
        // node_modules ist frisch (gerade installiert), das Projekt selbst ruht seit 2021.
        ScanResult scan = await ScanAsync(tree);

        DevArtifact artifact = Assert.Single(DevArtifactFinder.Find(scan.Root, CancellationToken.None));

        Assert.Equal(sourceTime, artifact.ProjectActivityUtc);
        Assert.True(artifact.Node.NewestWriteUtc > sourceTime);
    }

    [Fact]
    public async Task Tooling_folders_are_not_searched()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"AppData\Local\ext\package.json", 10);
        tree.CreateFile(@"AppData\Local\ext\node_modules\a.js", 5_000);
        tree.CreateFile(@".vscode\extensions\e\package.json", 10);
        tree.CreateFile(@".vscode\extensions\e\node_modules\a.js", 5_000);
        tree.CreateFile(@"projekt\package.json", 10);
        tree.CreateFile(@"projekt\node_modules\a.js", 5_000);
        ScanResult scan = await ScanAsync(tree);

        DevArtifact artifact = Assert.Single(DevArtifactFinder.Find(scan.Root, CancellationToken.None));

        Assert.Contains("projekt", artifact.FullPath);
    }

    [Fact]
    public async Task Finder_honours_cancellation()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"p\package.json", 10);
        tree.CreateFile(@"p\node_modules\a.js", 10);
        ScanResult scan = await ScanAsync(tree);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => DevArtifactFinder.Find(scan.Root, cts.Token));
    }

    // ------------------------------------------------------------ Verlauf

    private static ScanSnapshot Snapshot(string root, long total, params (string Path, long Size)[] folders)
        => new(
            ScanSnapshot.CurrentVersion, root, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), total, 0, 50 * 1024 * 1024,
            folders.Select(f => new SnapshotFolder(f.Path, f.Size, 0)).ToArray());

    private const long MB = 1024 * 1024;

    [Fact]
    public async Task Builder_keeps_only_folders_above_the_minimum_and_the_root()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"gross\a.bin", 3 * MB);
        tree.CreateFile(@"gross\tief\b.bin", 3 * MB);
        tree.CreateFile(@"klein\c.bin", 100);
        ScanResult scan = await ScanAsync(tree);

        ScanSnapshot snapshot = SnapshotBuilder.Build(scan.Root, DateTime.UtcNow, minFolderBytes: 2 * MB);

        string[] paths = snapshot.Folders.Select(f => f.Path).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { tree.Root, tree.Combine("gross"), tree.Combine("gross", "tief") }.OrderBy(p => p).ToArray(), paths);
        Assert.Equal(scan.Root.TotalSizeOnDisk, snapshot.TotalSizeOnDisk);
    }

    [Fact]
    public async Task Store_roundtrips_and_lists_newest_first()
    {
        using var tree = new TempTree();
        tree.CreateFile(@"a\f.bin", 3 * MB);
        ScanResult scan = await ScanAsync(tree);
        string directory = tree.Combine("verlauf");
        ScanSnapshot first = SnapshotBuilder.Build(scan.Root, new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), 2 * MB);
        ScanSnapshot second = SnapshotBuilder.Build(scan.Root, new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc), 2 * MB);

        string path = SnapshotStore.Save(directory, first);
        SnapshotStore.Save(directory, second);
        ScanSnapshot loaded = SnapshotStore.Load(path);
        IReadOnlyList<SnapshotInfo> infos = SnapshotStore.List(directory, out IReadOnlyList<string> problems);

        Assert.Equal(first.RootPath, loaded.RootPath);
        Assert.Equal(first.TakenUtc, loaded.TakenUtc);
        Assert.Equal(first.Folders.Count, loaded.Folders.Count);
        Assert.Equal(first.Folders[0], loaded.Folders[0]);
        Assert.Empty(problems);
        Assert.Equal(2, infos.Count);
        Assert.True(infos[0].TakenUtc > infos[1].TakenUtc);
    }

    [Fact]
    public void Store_reports_broken_files_instead_of_hiding_them_and_load_throws()
    {
        using var tree = new TempTree();
        string directory = tree.CreateDirectory("verlauf");
        File.WriteAllText(Path.Combine(directory, "kaputt.json.gz"), "kein gzip");

        IReadOnlyList<SnapshotInfo> infos = SnapshotStore.List(directory, out IReadOnlyList<string> problems);

        Assert.Empty(infos);
        Assert.Contains("kaputt.json.gz", Assert.Single(problems));
        Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(Path.Combine(directory, "kaputt.json.gz")));
    }

    [Fact]
    public void List_of_a_missing_directory_is_empty()
    {
        using var tree = new TempTree();

        Assert.Empty(SnapshotStore.List(tree.Combine("nichts"), out IReadOnlyList<string> problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void Compare_reports_growth_and_shrinkage_sorted_by_size()
    {
        ScanSnapshot old = Snapshot(@"C:\", 1000 * MB, (@"C:\", 1000 * MB), (@"C:\Daten", 300 * MB), (@"C:\Medien", 500 * MB));
        ScanSnapshot now = Snapshot(@"C:\", 1300 * MB, (@"C:\", 1300 * MB), (@"C:\Daten", 700 * MB), (@"C:\Medien", 400 * MB));

        SnapshotComparison result = SnapshotComparer.Compare(old, now);

        Assert.Equal(300 * MB, result.TotalDelta);
        Assert.Equal(new[] { @"C:\Daten", @"C:\Medien" }, result.Changes.Select(c => c.Path));
        Assert.Equal(400 * MB, result.Changes[0].Delta);
        Assert.Equal(-100 * MB, result.Changes[1].Delta);
    }

    [Fact]
    public void Compare_shows_the_deepest_cause_instead_of_every_parent()
    {
        ScanSnapshot old = Snapshot(@"C:\", 1000 * MB, (@"C:\Users", 100 * MB), (@"C:\Users\me", 100 * MB), (@"C:\Users\me\Downloads", 60 * MB));
        ScanSnapshot now = Snapshot(@"C:\", 1500 * MB, (@"C:\Users", 500 * MB), (@"C:\Users\me", 500 * MB), (@"C:\Users\me\Downloads", 460 * MB));

        SnapshotComparison result = SnapshotComparer.Compare(old, now);

        FolderChange change = Assert.Single(result.Changes);
        Assert.Equal(@"C:\Users\me\Downloads", change.Path);
        Assert.Equal(400 * MB, change.Delta);
    }

    [Fact]
    public void Compare_keeps_a_parent_when_several_children_share_its_change()
    {
        ScanSnapshot old = Snapshot(@"C:\", 1000 * MB, (@"C:\P", 100 * MB), (@"C:\P\A", 50 * MB), (@"C:\P\B", 50 * MB));
        ScanSnapshot now = Snapshot(@"C:\", 1200 * MB, (@"C:\P", 300 * MB), (@"C:\P\A", 150 * MB), (@"C:\P\B", 150 * MB));

        SnapshotComparison result = SnapshotComparer.Compare(old, now);

        Assert.Contains(result.Changes, c => c.Path == @"C:\P" && c.Delta == 200 * MB);
    }

    [Fact]
    public void Compare_treats_a_folder_missing_on_one_side_as_below_the_minimum()
    {
        ScanSnapshot old = Snapshot(@"C:\", 100 * MB, (@"C:\", 100 * MB));
        ScanSnapshot now = Snapshot(@"C:\", 400 * MB, (@"C:\", 400 * MB), (@"C:\Neu", 300 * MB));

        FolderChange change = Assert.Single(SnapshotComparer.Compare(old, now).Changes);

        Assert.Equal(@"C:\Neu", change.Path);
        Assert.Equal(0, change.OldBytes);
        Assert.Equal(300 * MB, change.NewBytes);
    }

    [Fact]
    public void Compare_ignores_changes_below_the_threshold_and_handles_identical_snapshots()
    {
        ScanSnapshot a = Snapshot(@"C:\", 500 * MB, (@"C:\", 500 * MB), (@"C:\X", 100 * MB));
        ScanSnapshot b = Snapshot(@"C:\", 505 * MB, (@"C:\", 505 * MB), (@"C:\X", 105 * MB));

        Assert.Empty(SnapshotComparer.Compare(a, b).Changes);
        Assert.Empty(SnapshotComparer.Compare(a, a).Changes);
    }
}
