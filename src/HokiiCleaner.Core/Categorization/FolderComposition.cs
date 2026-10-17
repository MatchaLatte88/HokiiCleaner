using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Core.Categorization;

/// <summary>Eine Datei direkt in einem Ordner mit ihrer Kategorie.</summary>
public readonly record struct OwnFile(FileEntry File, FileCategory Category);

/// <summary>Belegung eines Unterordners (rekursiv) aufgeteilt nach Kategorien, ohne Hardlink-Duplikate.</summary>
public sealed class ChildComposition
{
    internal ChildComposition(DirectoryNode node, long[] sizeByCategory)
    {
        Node = node;
        SizeByCategory = sizeByCategory;
        TotalSizeOnDisk = sizeByCategory.Sum();
        Trail = HeavyTrail.Find(node);
    }

    public DirectoryNode Node { get; }

    /// <summary>Spur zum dominierenden Inhalt des Ordners; <c>null</c>, wenn nichts dominiert.</summary>
    public HeavyTrail? Trail { get; }

    /// <summary>Belegung je Kategorie, indiziert mit <c>(int)FileCategory</c>.</summary>
    public IReadOnlyList<long> SizeByCategory { get; }

    public long TotalSizeOnDisk { get; }

    public long SizeOf(FileCategory category) => SizeByCategory[(int)category];

    /// <summary>Kategorie mit der größten Belegung; <c>null</c> bei leerem Ordner.</summary>
    public FileCategory? Dominant => CompositionMath.Dominant(SizeByCategory);
}

/// <summary>Zusammensetzung eines Ordners: seine direkten Dateien und jeder direkte Unterordner nach Kategorien.</summary>
public sealed class FolderComposition
{
    internal FolderComposition(
        DirectoryNode folder,
        long[] ownFilesSizeByCategory,
        IReadOnlyList<OwnFile> ownFiles,
        IReadOnlyList<ChildComposition> children)
    {
        Folder = folder;
        OwnFilesSizeByCategory = ownFilesSizeByCategory;
        OwnFiles = ownFiles;
        Children = children;

        var totals = new long[ownFilesSizeByCategory.Length];
        for (int i = 0; i < totals.Length; i++)
        {
            totals[i] = ownFilesSizeByCategory[i] + children.Sum(c => c.SizeByCategory[i]);
        }

        SizeByCategory = totals;
        TotalSizeOnDisk = totals.Sum();
    }

    public DirectoryNode Folder { get; }

    /// <summary>Belegung der Dateien, die direkt in diesem Ordner liegen, je Kategorie.</summary>
    public IReadOnlyList<long> OwnFilesSizeByCategory { get; }

    /// <summary>Die Dateien direkt in diesem Ordner mit Kategorie, ohne Hardlink-Duplikate, in Scan-Reihenfolge.</summary>
    public IReadOnlyList<OwnFile> OwnFiles { get; }

    public IReadOnlyList<ChildComposition> Children { get; }

    /// <summary>Belegung des gesamten Ordners je Kategorie.</summary>
    public IReadOnlyList<long> SizeByCategory { get; }

    public long TotalSizeOnDisk { get; }

    public long OwnFilesSizeOf(FileCategory category) => OwnFilesSizeByCategory[(int)category];

    public FileCategory? OwnFilesDominant => CompositionMath.Dominant(OwnFilesSizeByCategory);
}

public static class CompositionMath
{
    /// <summary>Kategorie mit der größten Belegung; <c>null</c>, wenn nirgends etwas belegt ist.</summary>
    public static FileCategory? Dominant(IReadOnlyList<long> sizes)
    {
        int best = -1;
        long bestSize = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            if (sizes[i] > bestSize)
            {
                bestSize = sizes[i];
                best = i;
            }
        }

        return best < 0 ? null : (FileCategory)best;
    }
}
