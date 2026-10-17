using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Core.Categorization;

/// <summary>
/// Spur zum größten Inhalt eines Ordners: die Namen der Unterordner, denen man folgt, und am Ende ggf. die Datei,
/// die den Ordner dominiert.
/// </summary>
/// <param name="Names">Namen entlang der Spur, ab dem untersuchten Ordner (ohne ihn selbst), nie leer.</param>
/// <param name="SizeOnDisk">Belegung des letzten Eintrags der Spur.</param>
/// <param name="EndsInFile">True, wenn der letzte Eintrag eine Datei ist, sonst ein Ordner.</param>
public sealed record HeavyTrail(IReadOnlyList<string> Names, long SizeOnDisk, bool EndsInFile)
{
    private const int MaxDepth = 8;

    /// <summary>
    /// Folgt vom Ordner aus immer dem größten Eintrag (Unterordner oder Datei), solange er mehr als die Hälfte
    /// seines Ordners belegt. <c>null</c>, wenn schon im ersten Schritt nichts dominiert (oder der Ordner leer ist).
    /// </summary>
    public static HeavyTrail? Find(DirectoryNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var names = new List<string>();
        long size = 0;
        bool endsInFile = false;
        DirectoryNode node = folder;

        while (names.Count < MaxDepth && node.TotalSizeOnDisk > 0)
        {
            DirectoryNode? bestChild = null;
            foreach (DirectoryNode child in node.Children)
            {
                if (bestChild is null || child.TotalSizeOnDisk > bestChild.TotalSizeOnDisk)
                {
                    bestChild = child;
                }
            }

            FileEntry? bestFile = null;
            foreach (FileEntry file in node.Files)
            {
                if (!file.IsHardLinkDuplicate && (bestFile is null || file.SizeOnDisk > bestFile.Value.SizeOnDisk))
                {
                    bestFile = file;
                }
            }

            long childSize = bestChild?.TotalSizeOnDisk ?? 0;
            long fileSize = bestFile?.SizeOnDisk ?? 0;
            long largest = Math.Max(childSize, fileSize);
            if (largest * 2 <= node.TotalSizeOnDisk)
            {
                break;
            }

            if (fileSize >= childSize)
            {
                names.Add(bestFile!.Value.Name);
                size = fileSize;
                endsInFile = true;
                break;
            }

            names.Add(bestChild!.Name);
            size = childSize;
            node = bestChild;
        }

        return names.Count == 0 ? null : new HeavyTrail(names, size, endsInFile);
    }
}
