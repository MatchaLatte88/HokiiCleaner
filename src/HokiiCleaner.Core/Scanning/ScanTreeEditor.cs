namespace HokiiCleaner.Core.Scanning;

/// <summary>Was aus dem Scan-Baum genommen wurde. Verändert nur den Speicher, nie das Dateisystem.</summary>
/// <param name="Node">Der herausgenommene Ordner; <c>null</c> bei einer Datei.</param>
/// <param name="File">Die herausgenommene Datei; <c>null</c> bei einem Ordner.</param>
public sealed record RemovedEntry(
    string FullPath,
    bool IsDirectory,
    long SizeOnDisk,
    long LogicalSize,
    long FileCount,
    DirectoryNode? Node,
    FileEntry? File);

/// <summary>
/// Hält den Scan-Baum aktuell, wenn HokiiCleaner selbst Dateien verschoben hat: Einträge entfernen, ihre Größen bei allen
/// übergeordneten Ordnern abziehen und einen Ordner (den Papierkorb) durch eine frisch gelesene Kopie ersetzen.
/// Der Baum wird dabei nur im Speicher verändert; die Listen werden ersetzt statt verändert, damit gleichzeitig lesende
/// Aufgaben nicht stolpern.
/// </summary>
public static class ScanTreeEditor
{
    /// <summary>
    /// Nimmt die Datei oder den Ordner mit dem absoluten Pfad aus dem Baum. <c>null</c>, wenn der Pfad nicht im Scan liegt.
    /// Die Wurzel selbst lässt sich nicht entfernen.
    /// </summary>
    public static RemovedEntry? Remove(DirectoryNode root, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string normalized = Path.GetFullPath(fullPath).TrimEnd('\\');
        DirectoryNode? directory = root.FindDescendant(normalized);
        if (directory is not null)
        {
            if (directory == root || directory.Parent is not DirectoryNode parent)
            {
                throw new ArgumentException("Die Scan-Wurzel lässt sich nicht entfernen.", nameof(fullPath));
            }

            if (!parent.DetachChild(directory))
            {
                return null;
            }

            long files = directory.TotalFileCount;
            Adjust(parent, -directory.TotalSizeOnDisk, -directory.TotalLogicalSize, -files, -(directory.TotalSubdirectoryCount + 1));
            return new RemovedEntry(normalized, true, directory.TotalSizeOnDisk, directory.TotalLogicalSize, files, directory, null);
        }

        string? parentPath = Path.GetDirectoryName(normalized);
        DirectoryNode? folder = parentPath is null ? null : root.FindDescendant(parentPath);
        if (folder is null)
        {
            return null;
        }

        string name = Path.GetFileName(normalized);
        FileEntry[] entries = folder.Files;
        int index = Array.FindIndex(entries, f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        FileEntry file = entries[index];
        var remaining = new FileEntry[entries.Length - 1];
        Array.Copy(entries, 0, remaining, 0, index);
        Array.Copy(entries, index + 1, remaining, index, entries.Length - index - 1);
        folder.Files = remaining;

        // Ein Hardlink-Duplikat war nie in den Summen; nur die Dateizahl zählt es mit.
        long size = file.IsHardLinkDuplicate ? 0 : file.SizeOnDisk;
        long logical = file.IsHardLinkDuplicate ? 0 : file.Length;
        Adjust(folder, -size, -logical, -1, 0);
        return new RemovedEntry(normalized, false, size, logical, 1, null, file);
    }

    /// <summary>
    /// Ersetzt den Inhalt von <paramref name="target"/> durch den einer frisch gelesenen Kopie desselben Ordners
    /// (<paramref name="fresh"/>), z. B. den Papierkorb-Ordner nach dem Verschieben. Die Summen darüber werden nachgeführt.
    /// </summary>
    public static void ReplaceContents(DirectoryNode target, DirectoryNode fresh)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fresh);

        ClearContents(target);
        var children = new List<DirectoryNode>(fresh.Children.Count);
        foreach (DirectoryNode child in fresh.Children)
        {
            children.Add(Clone(child, child.Name, target));
        }

        target.ReplaceChildren(children);
        target.Files = (FileEntry[])fresh.Files.Clone();
        target.State = fresh.State;
        target.ErrorMessage = fresh.ErrorMessage;
        Adjust(target, fresh.TotalSizeOnDisk, fresh.TotalLogicalSize, fresh.TotalFileCount, fresh.TotalSubdirectoryCount);
    }

    /// <summary>Findet den Ordner oder legt ihn samt fehlenden Zwischenordnern leer an. <c>null</c>, wenn er außerhalb des Scans liegt.</summary>
    public static DirectoryNode? EnsureFolder(DirectoryNode root, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string rootPath = root.FullPath.TrimEnd('\\');
        string target = Path.GetFullPath(fullPath).TrimEnd('\\');
        if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
            || (target.Length > rootPath.Length && target[rootPath.Length] != '\\'))
        {
            return null;
        }

        DirectoryNode node = root;
        foreach (string segment in target.Substring(rootPath.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryNode? next = node.Children.FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new DirectoryNode(segment, node) { ProbeHardLinks = node.ProbeHardLinks };
                node.InsertChild(next);
                Adjust(node, 0, 0, 0, 1);
            }

            node = next;
        }

        return node;
    }

    /// <summary>Entfernt alles unterhalb des Ordners (Inhalt, nicht den Ordner selbst), z. B. nach dem Leeren des Papierkorbs.</summary>
    public static void ClearContents(DirectoryNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        long onDisk = folder.TotalSizeOnDisk;
        long logical = folder.TotalLogicalSize;
        long files = folder.TotalFileCount;
        long subdirectories = folder.TotalSubdirectoryCount;

        foreach (DirectoryNode child in folder.Children.ToArray())
        {
            folder.DetachChild(child);
        }

        folder.Files = Array.Empty<FileEntry>();

        // Alles, was der Ordner insgesamt zählte, verschwindet bei ihm und bei allen Ordnern darüber.
        Adjust(folder, -onDisk, -logical, -files, -subdirectories);
    }

    /// <summary>Wahr, wenn der Ordner noch über seine Eltern bis zur Wurzel erreichbar ist (also nicht herausgenommen wurde).</summary>
    public static bool IsAttached(DirectoryNode root, DirectoryNode node)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(node);

        for (DirectoryNode current = node; current != root; current = current.Parent!)
        {
            if (current.Parent is not DirectoryNode parent || !parent.Children.Contains(current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verschiebt die Summen eines Ordners und aller Ordner darüber und bestimmt deren jüngste Änderung aus dem Inhalt neu.
    /// Dateien und Unterordner müssen dafür schon im Zustand nach der Änderung sein.
    /// </summary>
    private static void Adjust(DirectoryNode start, long onDisk, long logical, long files, long subdirectories)
    {
        for (DirectoryNode? node = start; node is not null; node = node.Parent)
        {
            node.TotalSizeOnDisk += onDisk;
            node.TotalLogicalSize += logical;
            node.TotalFileCount += files;
            node.TotalSubdirectoryCount += subdirectories;
            node.NewestWriteUtc = Newest(node);
        }
    }

    private static DateTime Newest(DirectoryNode node)
    {
        DateTime newest = DateTime.MinValue;
        foreach (FileEntry file in node.Files)
        {
            if (!file.IsHardLinkDuplicate && file.LastWriteUtc > newest)
            {
                newest = file.LastWriteUtc;
            }
        }

        foreach (DirectoryNode child in node.Children)
        {
            if (child.NewestWriteUtc > newest)
            {
                newest = child.NewestWriteUtc;
            }
        }

        return newest;
    }

    private static DirectoryNode Clone(DirectoryNode source, string name, DirectoryNode parent)
    {
        var copy = new DirectoryNode(name, parent)
        {
            ProbeHardLinks = source.ProbeHardLinks,
            State = source.State,
            ErrorMessage = source.ErrorMessage,
            LinkTarget = source.LinkTarget,
            Files = (FileEntry[])source.Files.Clone(),
            TotalSizeOnDisk = source.TotalSizeOnDisk,
            TotalLogicalSize = source.TotalLogicalSize,
            TotalFileCount = source.TotalFileCount,
            TotalSubdirectoryCount = source.TotalSubdirectoryCount,
            NewestWriteUtc = source.NewestWriteUtc,
        };

        foreach (DirectoryNode child in source.Children)
        {
            copy.AddChild(Clone(child, child.Name, copy));
        }

        return copy;
    }
}
