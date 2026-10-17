using System.Text.Json;
using System.Text.Json.Serialization;
using HokiiCleaner.Core.Diagnostics;

namespace HokiiCleaner.Core.Export;

/// <summary>JSON-Export des Langsam-Checks: die Befunde mit Messwert, Schwelle, Erklärung und Quelle.</summary>
public static class ReportJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Umlaute lesbar lassen
    };

    /// <param name="createdLocal">Zeitpunkt des Exports; wird als Parameter übergeben, damit die Funktion testbar bleibt.</param>
    public static string Serialize(SlownessReport report, DateTimeOffset createdLocal)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = new
        {
            Tool = "HokiiCleaner",
            Erstellt = createdLocal,
            Administratorrechte = report.Snapshot.IsElevated,
            DauerSekunden = Math.Round(report.Elapsed.TotalSeconds, 1),
            Befunde = report.Checks.Select(c => new
            {
                c.Id,
                Bereich = c.Area,
                Ergebnis = c.Outcome,
                c.Title,
                c.Measurement,
                c.Threshold,
                c.Explanation,
                c.Source,
                c.Details,
            }),
        };

        return JsonSerializer.Serialize(document, Options);
    }
}
