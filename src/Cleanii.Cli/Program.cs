using System.Globalization;
using Cleanii.Core;
using Cleanii.Core.Categorization;
using Cleanii.Core.Scanning;

// Test-Host für die Scan-Engine: Zahlen ausgeben, die sich gegen Explorer/PowerShell prüfen lassen.
// Aufruf: Cleanii.Cli <pfad> [--top N] [--parallelism N] [--quiet] [--issues] [--category NAME]

if (args.Length > 0 && args[0] == "slow")
{
    return await SlowCheck.RunAsync(args.Skip(1).ToArray());
}

if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Aufruf: Cleanii.Cli <pfad> [--top N] [--parallelism N] [--quiet] [--issues]");
    return 2;
}

string path = args[0];
int top = 10;
var options = new ScanOptions();
bool quiet = false;
bool allIssues = false;
FileCategory? showCategory = null;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--top":
            top = ParseInt(args, ref i);
            break;
        case "--parallelism":
            options = options with { Parallelism = ParseInt(args, ref i) };
            break;
        case "--quiet":
            quiet = true;
            break;
        case "--issues":
            allIssues = true;
            break;
        case "--category":
            if (i + 1 >= args.Length || !Enum.TryParse(args[i + 1], ignoreCase: true, out FileCategory parsed))
            {
                Console.Error.WriteLine($"--category erwartet einen Namen aus: {string.Join(", ", Enum.GetNames<FileCategory>())}");
                return 2;
            }

            showCategory = parsed;
            i++;
            break;
        default:
            Console.Error.WriteLine($"Unbekanntes Argument: {args[i]}");
            return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Fortschritt nur bei echter Konsole (bei Umleitung gibt es keine Fensterbreite und die Zeile wäre Rauschen).
bool showProgress = !quiet && !Console.IsErrorRedirected;
var progress = showProgress ? new ConsoleProgress() : null;

ScanResult result;
try
{
    result = await DiskScanner.ScanAsync(path, options, progress, cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("\nScan abgebrochen.");
    return 130;
}
finally
{
    if (showProgress)
    {
        Console.Error.Write("\r" + new string(' ', Math.Max(1, Console.WindowWidth - 1)) + "\r");
    }
}

DirectoryNode root = result.Root;
Console.WriteLine($"Pfad:               {root.FullPath}");
Console.WriteLine($"Dauer:              {result.Elapsed.TotalSeconds:N1} s");
Console.WriteLine($"Clustergröße:       {result.ClusterSize:N0} Byte");
Console.WriteLine($"Dateien:            {root.TotalFileCount:N0}");
Console.WriteLine($"Unterordner:        {root.TotalSubdirectoryCount:N0}");
Console.WriteLine($"Logische Größe:     {root.TotalLogicalSize:N0} Byte ({FormatSize(root.TotalLogicalSize)})");
Console.WriteLine($"Größe auf Datenträger: {root.TotalSizeOnDisk:N0} Byte ({FormatSize(root.TotalSizeOnDisk)})");
Console.WriteLine($"Hardlink-Duplikate: {result.HardLinkDuplicateFiles:N0} Dateien, {FormatSize(result.HardLinkDuplicateBytes)} nicht doppelt gezählt");
Console.WriteLine($"Probleme:           {result.Issues.Count:N0}");

CategoryBreakdown categories = CategoryAnalyzer.Analyze(root);
Console.WriteLine();
Console.WriteLine("Kategorien (Größe auf Datenträger):");
foreach (FileCategory category in Enum.GetValues<FileCategory>().OrderByDescending(c => categories[c].SizeOnDisk))
{
    CategoryTotals totals = categories[category];
    double share = categories.TotalSizeOnDisk == 0 ? 0 : 100.0 * totals.SizeOnDisk / categories.TotalSizeOnDisk;
    Console.WriteLine($"  {FormatSize(totals.SizeOnDisk),12}  {share,5:N1} %  {totals.FileCount,10:N0} Dateien  {category}");
}

if (showCategory is FileCategory selected)
{
    Console.WriteLine();
    Console.WriteLine($"Größte {top} Dateien der Kategorie {selected}:");
    foreach (TopFile item in categories.TopFiles(selected).Take(top))
    {
        Console.WriteLine($"  {FormatSize(item.File.SizeOnDisk),12}  {item.FullPath}");
    }
}

Console.WriteLine();
Console.WriteLine("Größte Einträge der obersten Ebene (Größe auf Datenträger):");
foreach (DirectoryNode child in root.Children.OrderByDescending(c => c.TotalSizeOnDisk).Take(top))
{
    string state = child.State == DirectoryState.Scanned ? string.Empty : $"  [{child.State}]";
    Console.WriteLine($"  {FormatSize(child.TotalSizeOnDisk),12}  {child.TotalFileCount,10:N0} Dateien  {child.Name}{state}");
}

long looseBytes = root.Files.Sum(f => f.IsHardLinkDuplicate ? 0 : f.SizeOnDisk);
if (root.Files.Length > 0)
{
    Console.WriteLine($"  {FormatSize(looseBytes),12}  {root.Files.Length,10:N0} Dateien  (direkt im Wurzelordner)");
}

Console.WriteLine();
Console.WriteLine($"Größte {top} Dateien:");
var largest = new PriorityQueue<(DirectoryNode Dir, FileEntry File), long>();
Stack<DirectoryNode> pending = new();
pending.Push(root);
while (pending.Count > 0)
{
    DirectoryNode node = pending.Pop();
    foreach (FileEntry file in node.Files)
    {
        if (file.IsHardLinkDuplicate)
        {
            continue;
        }

        largest.Enqueue((node, file), file.SizeOnDisk);
        if (largest.Count > top)
        {
            largest.Dequeue();
        }
    }

    foreach (DirectoryNode child in node.Children)
    {
        pending.Push(child);
    }
}

var largestSorted = new List<(DirectoryNode Dir, FileEntry File)>();
while (largest.Count > 0)
{
    largestSorted.Add(largest.Dequeue());
}

largestSorted.Reverse();
foreach (var (dir, file) in largestSorted)
{
    Console.WriteLine($"  {FormatSize(file.SizeOnDisk),12}  {Path.Combine(dir.FullPath, file.Name)}");
}

if (result.Issues.Count > 0)
{
    Console.WriteLine();
    int shown = allIssues ? result.Issues.Count : Math.Min(10, result.Issues.Count);
    Console.WriteLine($"Probleme ({shown:N0} von {result.Issues.Count:N0}):");
    foreach (ScanIssue issue in result.Issues.Take(shown))
    {
        Console.WriteLine($"  {issue.Path}: {issue.Message}");
    }
}

return 0;

static int ParseInt(string[] args, ref int i)
{
    string option = args[i];
    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < 1)
    {
        throw new ArgumentException($"{option} erwartet eine positive ganze Zahl.");
    }

    i++;
    return value;
}

static string FormatSize(long bytes) => ByteFormat.Format(bytes);

internal sealed class ConsoleProgress : IProgress<ScanProgress>
{
    public void Report(ScanProgress p)
    {
        string dir = p.CurrentDirectory;
        int width = Math.Max(40, Console.WindowWidth - 1);
        string text = $"{p.Files:N0} Dateien, {p.Directories:N0} Ordner, {p.Elapsed.TotalSeconds:N0} s  {dir}";
        if (text.Length > width)
        {
            text = text.Substring(0, width);
        }

        Console.Error.Write("\r" + text.PadRight(width));
    }
}
