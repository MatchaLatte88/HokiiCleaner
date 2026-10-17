using System.Globalization;
using System.Text;

namespace Cleanii.Actions;

/// <summary>Schreibt je Löschlauf ein Protokoll: was gelöscht wurde (je Ziel) und alles, was übersprungen wurde.</summary>
public static class CleanLog
{
    public static string Write(string directory, CleanResult result)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"aufraeumen-{result.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}.txt");

        var text = new StringBuilder();
        CultureInfo culture = CultureInfo.InvariantCulture;
        text.AppendLine($"Cleanii Aufräumen, gestartet {result.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, beendet {result.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        text.AppendLine(result.Cancelled ? "Abgebrochen: ja" : "Abgebrochen: nein");
        text.AppendLine($"Gelöscht: {result.DeletedFiles.ToString("N0", culture)} Dateien, {result.DeletedBytes.ToString("N0", culture)} Byte");
        text.AppendLine(result.FreedBytesMeasured is long freed
            ? $"Freier Platz gemessen: {freed.ToString("N0", culture)} Byte mehr als vorher"
            : "Freier Platz: nicht messbar");
        text.AppendLine("Einzelne gelöschte Dateien werden nicht aufgelistet.");

        foreach (TargetRun run in result.Runs)
        {
            text.AppendLine();
            text.AppendLine($"[{run.Target.Id}] {run.Target.Name}");
            text.AppendLine($"  Ordner: {string.Join("; ", run.Target.Roots)}");
            text.AppendLine($"  Gelöscht: {run.DeletedFiles.ToString("N0", culture)} Dateien, {run.DeletedBytes.ToString("N0", culture)} Byte; leere Ordner entfernt: {run.DirectoriesRemoved.ToString("N0", culture)}");
            text.AppendLine($"  Verknüpfungen übersprungen: {run.LinksSkipped.ToString("N0", culture)}");
            if (run.Problem is not null)
            {
                text.AppendLine($"  Abgelehnt: {run.Problem}");
            }

            foreach ((SkipReason reason, long count) in run.SkippedCounts.OrderBy(p => p.Key))
            {
                text.AppendLine($"  Übersprungen ({reason}): {count.ToString("N0", culture)}");
            }

            foreach (SkippedItem item in run.SkippedSample)
            {
                text.AppendLine($"    {item.Reason}: {item.Path}{(item.Detail is null ? string.Empty : " – " + item.Detail)}");
            }

            if (run.SkippedTotal > run.SkippedSample.Count)
            {
                text.AppendLine($"    … und {(run.SkippedTotal - run.SkippedSample.Count).ToString("N0", culture)} weitere");
            }
        }

        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        return path;
    }
}
