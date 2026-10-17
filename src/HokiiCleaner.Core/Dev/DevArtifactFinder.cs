using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Core.Dev;

public enum DevArtifactKind
{
    NodeModules,
    PythonVenv,
    DotNetBuild,
    RustTarget,
    MavenTarget,
    GradleBuild,
    NextBuild,
}

/// <summary>Ein Ordner, den ein Werkzeug aus dem Projekt neu erzeugen kann (Abhängigkeiten, Build-Ausgaben).</summary>
/// <param name="Node">Der Ordner selbst (z. B. <c>node_modules</c>).</param>
/// <param name="Project">Der Projektordner, in dem er liegt.</param>
/// <param name="ProjectActivityUtc">
/// Jüngste Änderung im Projekt ohne die erzeugten Ordner (Quellcode, Konfiguration, <c>.git</c>);
/// <see cref="DateTime.MinValue"/>, wenn das Projekt sonst leer ist.
/// </param>
/// <param name="Caution">Hinweis, wenn das Neuerzeugen unsicher ist (z. B. keine Liste der Python-Pakete gefunden); sonst <c>null</c>.</param>
public sealed record DevArtifact(
    DirectoryNode Node,
    DevArtifactKind Kind,
    string Title,
    string Regenerate,
    DirectoryNode Project,
    DateTime ProjectActivityUtc,
    string? Caution)
{
    public string FullPath => Node.FullPath;

    public long SizeOnDisk => Node.TotalSizeOnDisk;

    public long FileCount => Node.TotalFileCount;
}

/// <summary>
/// Findet im Scan Ordner, die sich aus ihrem Projekt neu erzeugen lassen, etwa <c>node_modules</c> neben einer <c>package.json</c>.
/// Rein lesend und ohne Dateizugriff: Erkannt wird allein an Ordnernamen und den Dateien daneben aus dem Scan. Ordner, die zu
/// installierten Programmen oder Werkzeugen gehören (AppData, Editor-Erweiterungen, Paketmanager), werden nicht durchsucht.
/// </summary>
public static class DevArtifactFinder
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information",
        ".vscode", ".vscode-insiders", ".cursor", ".windsurf", ".claude", ".npm", ".nuget", ".cargo", ".rustup", ".conda",
        ".pyenv", ".gradle", ".m2", "scoop", "miniconda3", "anaconda3", "site-packages",
    };

    /// <summary>Erzeugte Ordner zählen nicht als Aktivität des Projekts.</summary>
    private static readonly HashSet<string> GeneratedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".venv", "venv", "env", "obj", "bin", "target", "build", ".gradle", ".next", "dist", "__pycache__",
    };

    public static IReadOnlyList<DevArtifact> Find(DirectoryNode scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var result = new List<DevArtifact>();
        var pending = new Stack<DirectoryNode>();
        pending.Push(scope);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            DirectoryNode node = pending.Pop();
            foreach (DirectoryNode child in node.Children)
            {
                if (child.State != DirectoryState.Scanned || SkippedFolders.Contains(child.Name))
                {
                    continue;
                }

                DevArtifact? artifact = Classify(child, node);
                if (artifact is not null)
                {
                    result.Add(artifact);
                    continue; // darin liegt nichts Eigenes mehr
                }

                pending.Push(child);
            }
        }

        return result;
    }

    private static DevArtifact? Classify(DirectoryNode node, DirectoryNode parent)
    {
        bool ParentHas(Func<string, bool> match) => parent.Files.Any(f => match(f.Name));
        bool ParentHasFile(params string[] names) => ParentHas(n => names.Contains(n, StringComparer.OrdinalIgnoreCase));
        bool ParentHasExtension(params string[] extensions) => ParentHas(n => extensions.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase));

        string name = node.Name;
        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && ParentHasFile("package.json"))
        {
            return Create(node, parent, DevArtifactKind.NodeModules, "Node.js-Abhängigkeiten", "npm install (oder yarn/pnpm install) stellt den Ordner aus der package.json wieder her.", null);
        }

        if ((name.Equals(".venv", StringComparison.OrdinalIgnoreCase) || name.Equals("venv", StringComparison.OrdinalIgnoreCase) || name.Equals("env", StringComparison.OrdinalIgnoreCase))
            && node.Files.Any(f => f.Name.Equals("pyvenv.cfg", StringComparison.OrdinalIgnoreCase)))
        {
            bool hasList = ParentHasFile("requirements.txt", "pyproject.toml", "Pipfile", "setup.py", "environment.yml", "uv.lock", "poetry.lock");
            return Create(
                node, parent, DevArtifactKind.PythonVenv, "Python-Umgebung",
                "Mit python -m venv und pip install (oder uv sync) neu anlegbar.",
                hasList ? null : "Im Projekt liegt keine Liste der Pakete (requirements.txt, pyproject.toml …). Die genauen Versionen wären nicht reproduzierbar.");
        }

        if ((name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
            && ParentHasExtension(".csproj", ".fsproj", ".vbproj", ".vcxproj"))
        {
            return Create(node, parent, DevArtifactKind.DotNetBuild, ".NET-Build-Ausgabe", "dotnet build (oder Visual Studio) erzeugt den Ordner neu.", null);
        }

        if (name.Equals("target", StringComparison.OrdinalIgnoreCase) && ParentHasFile("Cargo.toml"))
        {
            return Create(node, parent, DevArtifactKind.RustTarget, "Rust-Build-Ausgabe", "cargo build erzeugt den Ordner neu; das kann bei großen Projekten lange dauern.", null);
        }

        if (name.Equals("target", StringComparison.OrdinalIgnoreCase) && ParentHasFile("pom.xml"))
        {
            return Create(node, parent, DevArtifactKind.MavenTarget, "Maven-Build-Ausgabe", "mvn package erzeugt den Ordner neu.", null);
        }

        if (name.Equals("build", StringComparison.OrdinalIgnoreCase) && ParentHasFile("build.gradle", "build.gradle.kts"))
        {
            return Create(node, parent, DevArtifactKind.GradleBuild, "Gradle-Build-Ausgabe", "gradle build erzeugt den Ordner neu.", null);
        }

        if (name.Equals(".next", StringComparison.OrdinalIgnoreCase) && ParentHasFile("package.json"))
        {
            return Create(node, parent, DevArtifactKind.NextBuild, "Next.js-Build-Ausgabe", "npm run build (oder npm run dev) erzeugt den Ordner neu.", null);
        }

        return null;
    }

    private static DevArtifact Create(DirectoryNode node, DirectoryNode project, DevArtifactKind kind, string title, string regenerate, string? caution)
        => new(node, kind, title, regenerate, project, ProjectActivity(project), caution);

    private static DateTime ProjectActivity(DirectoryNode project)
    {
        DateTime newest = DateTime.MinValue;
        foreach (FileEntry file in project.Files)
        {
            if (file.LastWriteUtc > newest)
            {
                newest = file.LastWriteUtc;
            }
        }

        foreach (DirectoryNode child in project.Children)
        {
            if (!GeneratedFolders.Contains(child.Name) && child.NewestWriteUtc > newest)
            {
                newest = child.NewestWriteUtc;
            }
        }

        return newest;
    }
}
