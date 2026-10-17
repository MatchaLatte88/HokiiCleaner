namespace HokiiCleaner.Core.Diagnostics;

/// <summary>
/// Ergebnis einer Prüfung. Die Reihenfolge ist die Schwere (aufsteigend): <see cref="Ok"/> ist unauffällig,
/// <see cref="Unavailable"/> heißt "nicht messbar" (mit Grund), <see cref="Critical"/> ist am schwersten.
/// </summary>
public enum CheckOutcome
{
    Ok,
    Info,
    Unavailable,
    Warning,
    Critical,
}

public enum CheckArea
{
    Drive,
    Startup,
    Processes,
    System,
}

/// <summary>Ein Befund des Langsam-Checks mit Messwert, Schwelle, Erklärung und Quelle.</summary>
/// <param name="Id">Stabile Kennung, z. B. <c>drive.free.C:\</c>.</param>
/// <param name="Measurement">Was gemessen wurde, in einem Satz.</param>
/// <param name="Threshold">Ab wann es auffällt; <c>null</c> bei reinen Fakten oder nicht messbaren Prüfungen.</param>
/// <param name="Explanation">Warum das für die Geschwindigkeit zählt, oder bei <see cref="CheckOutcome.Unavailable"/> der Grund.</param>
/// <param name="Source">Woher der Messwert stammt (API, Registry, Stichprobe).</param>
/// <param name="Details">Einzelposten, z. B. die größten Verbraucher.</param>
public sealed record Check(
    string Id,
    CheckArea Area,
    CheckOutcome Outcome,
    string Title,
    string Measurement,
    string? Threshold,
    string Explanation,
    string Source,
    IReadOnlyList<string> Details)
{
    public bool NeedsAttention => Outcome is CheckOutcome.Warning or CheckOutcome.Critical;

    /// <summary>Sortierschlüssel für die Anzeige: Kritisch, Warnung, Info, nicht gemessen, unauffällig.</summary>
    public int DisplayRank => Outcome switch
    {
        CheckOutcome.Critical => 0,
        CheckOutcome.Warning => 1,
        CheckOutcome.Info => 2,
        CheckOutcome.Unavailable => 3,
        _ => 4,
    };
}
