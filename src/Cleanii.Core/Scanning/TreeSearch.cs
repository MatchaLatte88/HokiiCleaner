namespace Cleanii.Core.Scanning;

/// <summary>Ein Treffer der Namenssuche: ein Ordner oder eine Datei samt Belegung.</summary>
/// <param name="Folder">Bei einer Datei der Ordner, in dem sie liegt; bei einem Ordner der Ordner selbst.</param>
/// <param name="File">Die Datei; <c>null</c>, wenn der Treffer ein Ordner ist.</param>
/// <param name="SizeOnDisk">Belegung der Datei bzw. des gesamten Ordners samt Inhalt.</param>
public sealed record SearchHit(DirectoryNode Folder, FileEntry? File, long SizeOnDisk)
{
    public bool IsFile => File is not null;
}

/// <param name="Hits">Die größten Treffer nach Belegung, absteigend, höchstens so viele wie angefordert.</param>
/// <param name="TotalMatches">Alle Treffer, auch die nicht in <paramref name="Hits"/> enthaltenen.</param>
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, long TotalMatches);

/// <summary>Namenssuche im gescannten Baum. Arbeitet nur im Speicher, kein Dateisystemzugriff.</summary>
public static class TreeSearch
{
    /// <summary>
    /// Findet Ordner und Dateien unterhalb von <paramref name="start"/> (ohne ihn selbst), deren Name den Suchtext
    /// enthält (ohne Beachtung der Groß-/Kleinschreibung). Hardlink-Duplikate zählen nicht.
    /// </summary>
    public static SearchResult Find(DirectoryNode start, string query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var heap = new PriorityQueue<SearchHit, long>();
        long total = 0;
        var stack = new Stack<DirectoryNode>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryNode node = stack.Pop();

            foreach (FileEntry file in node.Files)
            {
                if (!file.IsHardLinkDuplicate && file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    total++;
                    Offer(heap, limit, new SearchHit(node, file, file.SizeOnDisk));
                }
            }

            foreach (DirectoryNode child in node.Children)
            {
                if (child.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    total++;
                    Offer(heap, limit, new SearchHit(child, null, child.TotalSizeOnDisk));
                }

                stack.Push(child);
            }
        }

        var hits = new List<SearchHit>(heap.Count);
        while (heap.Count > 0)
        {
            hits.Add(heap.Dequeue());
        }

        hits.Reverse();
        return new SearchResult(hits, total);
    }

    private static void Offer(PriorityQueue<SearchHit, long> heap, int limit, SearchHit hit)
    {
        if (heap.Count < limit)
        {
            heap.Enqueue(hit, hit.SizeOnDisk);
        }
        else if (heap.TryPeek(out _, out long smallest) && hit.SizeOnDisk > smallest)
        {
            heap.EnqueueDequeue(hit, hit.SizeOnDisk);
        }
    }
}
