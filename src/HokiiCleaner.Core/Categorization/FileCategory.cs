namespace HokiiCleaner.Core.Categorization;

/// <summary>
/// Kategorien der Typen-Ansicht. Die Reihenfolge ist die feste Reihenfolge im Messband
/// und bleibt zwischen Scans stabil, damit Läufe vergleichbar sind.
/// </summary>
public enum FileCategory
{
    Videos,
    Images,
    Audio,
    Documents,
    Archives,
    Installers,
    AiModels,
    VirtualDisks,
    Apps,
    TempAndCaches,
    OldWindows,
    System,
    RecycleBin,
    Other,
}
