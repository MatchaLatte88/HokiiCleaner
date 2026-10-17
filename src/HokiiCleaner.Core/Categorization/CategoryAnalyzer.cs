using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Core.Categorization;

public sealed record CategoryAnalyzerOptions
{
    /// <summary>Regelsatz; <c>null</c> = <see cref="CategoryRules.Default"/>.</summary>
    public CategoryRules? Rules { get; init; }

    /// <summary>
    /// Pfad, der als Laufwerkswurzel für die Pfadregeln gilt (<c>Windows</c>, <c>Users</c> …).
    /// <c>null</c> = Wurzel des Laufwerks, auf dem der Ordner liegt. Muss gleich oder über dem analysierten Ordner liegen.
    /// </summary>
    public string? RuleRoot { get; init; }

    /// <summary>Anzahl der gemerkten größten Dateien je Kategorie (nur für <see cref="CategoryAnalyzer.Analyze"/>).</summary>
    public int TopFilesPerCategory { get; init; } = 100;
}

/// <summary>Ordnet die Dateien eines gescannten Baums den Kategorien zu. Rein lesend, keine Dateisystemzugriffe.</summary>
public static class CategoryAnalyzer
{
    private static readonly int CategoryCount = Enum.GetValues<FileCategory>().Length;

    private readonly record struct Frame(
        DirectoryNode Node,
        CategoryRules.RuleNode? Rule,
        FileCategory? Override,
        bool IsRuleRoot);

    /// <summary>Summen und größte Dateien je Kategorie für den gesamten Teilbaum ab <paramref name="start"/>.</summary>
    public static CategoryBreakdown Analyze(DirectoryNode start, CategoryAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        options ??= new CategoryAnalyzerOptions();
        if (options.TopFilesPerCategory < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TopFilesPerCategory darf nicht negativ sein.");
        }

        (CategoryRules rules, Frame startFrame) = ResolveStart(start, options);

        var sizeOnDisk = new long[CategoryCount];
        var logical = new long[CategoryCount];
        var count = new long[CategoryCount];
        int topLimit = options.TopFilesPerCategory;
        var heaps = new PriorityQueue<TopFile, long>[CategoryCount];
        for (int i = 0; i < CategoryCount; i++)
        {
            heaps[i] = new PriorityQueue<TopFile, long>();
        }

        var stack = new Stack<Frame>();
        stack.Push(startFrame);
        while (stack.Count > 0)
        {
            Frame frame = stack.Pop();
            DirectoryNode node = frame.Node;

            foreach (FileEntry file in node.Files)
            {
                if (file.IsHardLinkDuplicate)
                {
                    continue;
                }

                int index = (int)Categorize(rules, frame, file);
                sizeOnDisk[index] += file.SizeOnDisk;
                logical[index] += file.Length;
                count[index]++;

                if (topLimit > 0)
                {
                    PriorityQueue<TopFile, long> heap = heaps[index];
                    if (heap.Count < topLimit)
                    {
                        heap.Enqueue(new TopFile(node, file), file.SizeOnDisk);
                    }
                    else if (heap.TryPeek(out _, out long smallest) && file.SizeOnDisk > smallest)
                    {
                        heap.EnqueueDequeue(new TopFile(node, file), file.SizeOnDisk);
                    }
                }
            }

            foreach (DirectoryNode child in node.Children)
            {
                stack.Push(ChildFrame(rules, frame, child));
            }
        }

        var totals = new CategoryTotals[CategoryCount];
        var topFiles = new IReadOnlyList<TopFile>[CategoryCount];
        for (int i = 0; i < CategoryCount; i++)
        {
            totals[i] = new CategoryTotals(sizeOnDisk[i], logical[i], count[i]);

            var sorted = new List<TopFile>(heaps[i].Count);
            while (heaps[i].Count > 0)
            {
                sorted.Add(heaps[i].Dequeue());
            }

            sorted.Reverse();
            topFiles[i] = sorted;
        }

        return new CategoryBreakdown(start, totals, topFiles);
    }

    /// <summary>
    /// Belegung je Kategorie für jeden direkten Unterordner und die Dateien direkt in <paramref name="folder"/>.
    /// Ein Durchlauf über den Teilbaum; Grundlage der Orte-Ansicht und ihres Typ-Filters.
    /// </summary>
    public static FolderComposition Compose(DirectoryNode folder, CategoryAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        (CategoryRules rules, Frame startFrame) = ResolveStart(folder, options ?? new CategoryAnalyzerOptions());

        var own = new long[CategoryCount];
        var ownFiles = new List<OwnFile>(folder.Files.Length);
        foreach (FileEntry file in folder.Files)
        {
            if (!file.IsHardLinkDuplicate)
            {
                FileCategory category = Categorize(rules, startFrame, file);
                own[(int)category] += file.SizeOnDisk;
                ownFiles.Add(new OwnFile(file, category));
            }
        }

        var children = new List<ChildComposition>(folder.Children.Count);
        var stack = new Stack<Frame>();
        foreach (DirectoryNode child in folder.Children)
        {
            var sizes = new long[CategoryCount];
            stack.Push(ChildFrame(rules, startFrame, child));
            while (stack.Count > 0)
            {
                Frame frame = stack.Pop();
                foreach (FileEntry file in frame.Node.Files)
                {
                    if (!file.IsHardLinkDuplicate)
                    {
                        sizes[(int)Categorize(rules, frame, file)] += file.SizeOnDisk;
                    }
                }

                foreach (DirectoryNode grandChild in frame.Node.Children)
                {
                    stack.Push(ChildFrame(rules, frame, grandChild));
                }
            }

            children.Add(new ChildComposition(child, sizes));
        }

        return new FolderComposition(folder, own, ownFiles, children);
    }

    private static (CategoryRules Rules, Frame Start) ResolveStart(DirectoryNode start, CategoryAnalyzerOptions options)
    {
        CategoryRules rules = options.Rules ?? CategoryRules.Default;
        string startPath = start.FullPath;
        string ruleRoot = options.RuleRoot is null
            ? Path.GetPathRoot(startPath) ?? throw new ArgumentException($"Kein Laufwerk oder Netzwerkpfad: {startPath}", nameof(start))
            : Path.GetFullPath(options.RuleRoot);

        string relative = Path.GetRelativePath(ruleRoot, startPath);
        if (relative == ".." || relative.StartsWith(@"..\", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"'{startPath}' liegt nicht unter der Regel-Wurzel '{ruleRoot}'.", nameof(options));
        }

        // Regelzustand des Startordners ermitteln, indem der Pfad von der Wurzel her abgelaufen wird.
        CategoryRules.RuleNode? rule = rules.PathRoot;
        FileCategory? inherited = null;
        bool isRuleRoot = relative == ".";
        if (!isRuleRoot)
        {
            foreach (string segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                (rule, inherited) = rules.Descend(rule, inherited, segment);
            }
        }

        return (rules, new Frame(start, rule, inherited, isRuleRoot));
    }

    private static Frame ChildFrame(CategoryRules rules, Frame parent, DirectoryNode child)
    {
        (CategoryRules.RuleNode? rule, FileCategory? categoryOverride) = rules.Descend(parent.Rule, parent.Override, child.Name);
        return new Frame(child, rule, categoryOverride, IsRuleRoot: false);
    }

    private static FileCategory Categorize(CategoryRules rules, Frame frame, FileEntry file)
    {
        if (frame.IsRuleRoot && rules.TryGetRootFile(file.Name, out FileCategory rootCategory))
        {
            return rootCategory;
        }

        if (frame.Rule is not null && frame.Rule.TryGetFile(file.Name, out FileCategory pathFileCategory))
        {
            return pathFileCategory;
        }

        return frame.Override ?? rules.ByExtension(file.Name);
    }

    /// <summary>
    /// Kategorie einer einzelnen Datei in <paramref name="folder"/>, mit denselben Pfadregeln wie bei
    /// <see cref="Analyze"/> und <see cref="Compose"/>. Läuft den Pfad des Ordners ab, ist also für wenige Dateien gedacht.
    /// </summary>
    public static FileCategory CategorizeFile(DirectoryNode folder, FileEntry file, CategoryAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        (CategoryRules rules, Frame frame) = ResolveStart(folder, options ?? new CategoryAnalyzerOptions());
        return Categorize(rules, frame, file);
    }
}
