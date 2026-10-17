using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.Core.Categorization;

/// <summary>Rangfolgen über gescannte Teilbäume. Rein lesend, keine Dateisystemzugriffe.</summary>
public static class FileRanking
{
    /// <summary>Die größten Dateien im Teilbaum nach Belegung, absteigend, ohne Hardlink-Duplikate.</summary>
    public static IReadOnlyList<TopFile> Largest(DirectoryNode start, int count)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return Array.Empty<TopFile>();
        }

        var heap = new PriorityQueue<TopFile, long>();
        var stack = new Stack<DirectoryNode>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            DirectoryNode node = stack.Pop();
            foreach (FileEntry file in node.Files)
            {
                if (file.IsHardLinkDuplicate)
                {
                    continue;
                }

                if (heap.Count < count)
                {
                    heap.Enqueue(new TopFile(node, file), file.SizeOnDisk);
                }
                else if (heap.TryPeek(out _, out long smallest) && file.SizeOnDisk > smallest)
                {
                    heap.EnqueueDequeue(new TopFile(node, file), file.SizeOnDisk);
                }
            }

            foreach (DirectoryNode child in node.Children)
            {
                stack.Push(child);
            }
        }

        var result = new List<TopFile>(heap.Count);
        while (heap.Count > 0)
        {
            result.Add(heap.Dequeue());
        }

        result.Reverse();
        return result;
    }
}
