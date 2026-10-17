using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Core.Categorization;

public readonly record struct CategoryTotals(long SizeOnDisk, long LogicalSize, long FileCount);

/// <summary>Eine der größten Dateien einer Kategorie; der Pfad wird erst bei Bedarf gebildet.</summary>
public readonly record struct TopFile(DirectoryNode Directory, FileEntry File)
{
    public string FullPath => Path.Combine(Directory.FullPath, File.Name);
}

public sealed class CategoryBreakdown
{
    private readonly CategoryTotals[] _totals;
    private readonly IReadOnlyList<TopFile>[] _topFiles;

    internal CategoryBreakdown(DirectoryNode root, CategoryTotals[] totals, IReadOnlyList<TopFile>[] topFiles)
    {
        Root = root;
        _totals = totals;
        _topFiles = topFiles;
        TotalSizeOnDisk = totals.Sum(t => t.SizeOnDisk);
        TotalLogicalSize = totals.Sum(t => t.LogicalSize);
        TotalFileCount = totals.Sum(t => t.FileCount);
    }

    /// <summary>Der analysierte Ordner.</summary>
    public DirectoryNode Root { get; }

    public CategoryTotals this[FileCategory category] => _totals[(int)category];

    /// <summary>Summe über alle Kategorien; entspricht <see cref="DirectoryNode.TotalSizeOnDisk"/> des analysierten Ordners.</summary>
    public long TotalSizeOnDisk { get; }

    public long TotalLogicalSize { get; }

    /// <summary>Anzahl Dateien ohne Hardlink-Duplikate.</summary>
    public long TotalFileCount { get; }

    /// <summary>Größte Dateien der Kategorie nach Belegung, absteigend.</summary>
    public IReadOnlyList<TopFile> TopFiles(FileCategory category) => _topFiles[(int)category];
}
