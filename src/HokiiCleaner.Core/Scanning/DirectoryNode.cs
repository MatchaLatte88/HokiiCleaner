namespace HokiiCleaner.Core.Scanning;

public enum DirectoryState
{
    /// <summary>Ordner wurde vollständig gelesen.</summary>
    Scanned,

    /// <summary>Ordner konnte nicht oder nur teilweise gelesen werden; Größen sind unvollständig (siehe <see cref="DirectoryNode.ErrorMessage"/>).</summary>
    Unreadable,

    /// <summary>Symlink oder Junction; nicht betreten, um Schleifen und Doppelzählung zu vermeiden.</summary>
    LinkSkipped,
}

/// <summary>Ein Ordner im Scan-Baum mit seinen Dateien und aggregierten Summen.</summary>
public sealed class DirectoryNode
{
    // Nach dem Scan wird die Liste nur noch durch eine neue ersetzt (siehe ScanTreeEditor), damit lesende Hintergrundaufgaben
    // (Suche, Duplikate) nie in eine veränderte Liste laufen.
    private List<DirectoryNode> _children = new();

    internal DirectoryNode(string name, DirectoryNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <summary>Ordnername; beim Wurzelknoten der vollständige Pfad.</summary>
    public string Name { get; }

    public DirectoryNode? Parent { get; }

    public IReadOnlyList<DirectoryNode> Children => _children;

    /// <summary>Dateien direkt in diesem Ordner.</summary>
    public FileEntry[] Files { get; internal set; } = Array.Empty<FileEntry>();

    public DirectoryState State { get; internal set; } = DirectoryState.Scanned;

    /// <summary>Fehlertext bei <see cref="DirectoryState.Unreadable"/>.</summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>Ziel bei <see cref="DirectoryState.LinkSkipped"/>, falls ermittelbar.</summary>
    public string? LinkTarget { get; internal set; }

    /// <summary>Summe der Belegung auf dem Datenträger inklusive Unterordnern (Hardlink-Duplikate ausgenommen).</summary>
    public long TotalSizeOnDisk { get; internal set; }

    /// <summary>Summe der logischen Größen inklusive Unterordnern (Hardlink-Duplikate ausgenommen).</summary>
    public long TotalLogicalSize { get; internal set; }

    /// <summary>Anzahl Dateien inklusive Unterordnern (Hardlink-Duplikate mitgezählt).</summary>
    public long TotalFileCount { get; internal set; }

    /// <summary>Anzahl Unterordner rekursiv, ohne diesen Ordner selbst.</summary>
    public long TotalSubdirectoryCount { get; internal set; }

    /// <summary>
    /// Jüngste Änderung einer Datei in diesem Ordner oder darunter (Hardlink-Duplikate ausgenommen);
    /// <see cref="DateTime.MinValue"/>, wenn es keine Dateien gibt.
    /// </summary>
    public DateTime NewestWriteUtc { get; internal set; }

    internal bool ProbeHardLinks { get; init; }

    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);

    /// <summary>
    /// Findet den Ordner mit dem absoluten Pfad im Teilbaum dieses Knotens; <c>null</c>, wenn er außerhalb liegt
    /// oder nicht gescannt wurde. Die Groß-/Kleinschreibung wird nicht beachtet.
    /// </summary>
    public DirectoryNode? FindDescendant(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string rootPath = FullPath.TrimEnd('\\');
        string target = Path.GetFullPath(fullPath).TrimEnd('\\');
        if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
            || (target.Length > rootPath.Length && target[rootPath.Length] != '\\'))
        {
            return null;
        }

        DirectoryNode node = this;
        foreach (string segment in target.Substring(rootPath.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryNode? next = null;
            foreach (DirectoryNode child in node.Children)
            {
                if (string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            node = next;
        }

        return node;
    }

    internal void AddChild(DirectoryNode child) => _children.Add(child);

    /// <summary>Hängt einen Ordner nach dem Scan an (ersetzt die Liste, statt sie zu verändern).</summary>
    internal void InsertChild(DirectoryNode child) => _children = new List<DirectoryNode>(_children) { child };

    /// <summary>Ersetzt alle Unterordner auf einmal (nach dem Scan).</summary>
    internal void ReplaceChildren(List<DirectoryNode> children) => _children = children;

    /// <summary>Nimmt einen Ordner nach dem Scan heraus (ersetzt die Liste, statt sie zu verändern).</summary>
    internal bool DetachChild(DirectoryNode child)
    {
        int index = _children.IndexOf(child);
        if (index < 0)
        {
            return false;
        }

        var copy = new List<DirectoryNode>(_children);
        copy.RemoveAt(index);
        _children = copy;
        return true;
    }
}
