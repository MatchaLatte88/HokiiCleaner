using System.Globalization;
using System.Text;

namespace HokiiCleaner.Actions;

/// <summary>
/// Schreibt je Verschieben ein Protokoll. Anders als beim endgültigen Löschen steht hier jeder verschobene Pfad, denn der
/// Papierkorb benennt seine Einträge um; das Protokoll zeigt, was wohin gegangen ist und was nicht verschoben werden konnte.
/// </summary>
public static class RecycleLog
{
    public static string Write(string directory, RecycleResult result, RecyclePlan plan)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"papierkorb-{result.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}.txt");
        CultureInfo culture = CultureInfo.InvariantCulture;

        var text = new StringBuilder();
        text.AppendLine($"HokiiCleaner Papierkorb, gestartet {result.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, beendet {result.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        text.AppendLine(result.Cancelled ? "Abgebrochen: ja" : "Abgebrochen: nein");
        text.AppendLine($"Verschoben: {result.MovedCount.ToString("N0", culture)} Einträge, {result.MovedBytes.ToString("N0", culture)} Byte (laut Scan)");
        text.AppendLine("Die Einträge liegen im Papierkorb und lassen sich dort wiederherstellen, bis er geleert wird.");
        if (result.BinGrowthLooksShort)
        {
            text.AppendLine($"Hinweis: Der Papierkorb zählt {(result.BinItemsAfter - result.BinItemsBefore)?.ToString("N0", culture)} neue Einträge, verschoben wurden {result.MovedCount.ToString("N0", culture)}.");
        }

        foreach (string note in plan.Notes)
        {
            text.AppendLine($"Hinweis: {note}");
        }

        text.AppendLine();
        text.AppendLine("[Verschoben]");
        foreach (RecycleItemResult item in result.Moved)
        {
            text.AppendLine($"  {(item.Item.IsDirectory ? "Ordner" : "Datei ")} {item.Item.SizeOnDisk.ToString("N0", culture),16} Byte  {item.Item.Path}");
        }

        var problems = result.Items.Where(i => i.Status != RecycleStatus.Moved).ToArray();
        if (problems.Length > 0 || plan.Refused.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("[Nicht verschoben]");
            foreach (RecycleRefusal refusal in plan.Refused)
            {
                text.AppendLine($"  Abgelehnt: {refusal.Item.Path} – {refusal.Reason}");
            }

            foreach (RecycleItemResult item in problems)
            {
                text.AppendLine($"  {item.Status}: {item.Item.Path}{(item.Detail is null ? string.Empty : " – " + item.Detail)}");
            }
        }

        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        return path;
    }
}
