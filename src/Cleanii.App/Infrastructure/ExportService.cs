using System.IO;
using Cleanii.Core.Export;
using Microsoft.Win32;

namespace Cleanii.App.Infrastructure;

/// <summary>
/// Speichert einen Export dort, wo der Benutzer es wählt. Das ist neben den Einstellungen die einzige Stelle, an der
/// Cleanii schreibt, und sie läuft nur auf ausdrücklichen Wunsch (Schaltfläche, Speichern-Dialog).
/// </summary>
public static class ExportService
{
    private const string CsvFilter = "CSV-Datei (*.csv)|*.csv";
    private const string JsonFilter = "JSON-Datei (*.json)|*.json";

    /// <summary>Fragt nach dem Speicherort und schreibt eine CSV-Datei. Liefert eine Meldung für die Statuszeile; <c>null</c> bei Abbruch im Dialog.</summary>
    public static string? SaveCsv(string title, string suggestedName, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
        => Save(title, suggestedName, CsvFilter, ".csv", path =>
        {
            using var writer = new StreamWriter(path, append: false, CsvWriter.Encoding);
            CsvWriter.Write(writer, header, rows);
        });

    public static string? SaveJson(string title, string suggestedName, string json)
        => Save(title, suggestedName, JsonFilter, ".json", path => File.WriteAllText(path, json, new System.Text.UTF8Encoding(false)));

    private static string? Save(string title, string suggestedName, string filter, string extension, Action<string> write)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            DefaultExt = extension,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        try
        {
            write(dialog.FileName);
            return $"Gespeichert: {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Export fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>Vorschlag für einen Dateinamen mit Datum, z. B. <c>Cleanii-Ordner-2026-10-16.csv</c>.</summary>
    public static string SuggestName(string topic, string extension)
        => $"Cleanii-{topic}-{DateTime.Now:yyyy-MM-dd}{extension}";
}
