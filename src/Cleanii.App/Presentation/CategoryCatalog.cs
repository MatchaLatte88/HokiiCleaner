using System.Windows.Media;
using Cleanii.Core.Categorization;

namespace Cleanii.App.Presentation;

/// <param name="Hint">Was passiert, wenn der Inhalt dieser Kategorie wegfällt. Orientierung, keine Empfehlung zum Löschen.</param>
public sealed record CategoryInfo(FileCategory Category, string Name, Brush Brush, string Hint);

/// <summary>
/// Anzeigenamen und Farben der Kategorien. Farben sind in OKLCH festgelegt und geprüft:
/// alle im sRGB-Gamut, Kontrast gegen #151921 mindestens 3,1:1, kleinster Farbabstand (OKLab) 0,08.
/// </summary>
public static class CategoryCatalog
{
    private static readonly IReadOnlyDictionary<FileCategory, CategoryInfo> Infos = Build();

    public static CategoryInfo Get(FileCategory category) => Infos[category];

    /// <summary>Farbe für Platz, der keiner Kategorie zugeordnet ist (entspricht dem gedimmten Text-Token).</summary>
    public static Brush UnattributedBrush { get; } = CreateBrush("#8D96A7");

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    private static Dictionary<FileCategory, CategoryInfo> Build()
    {
        var entries = new (FileCategory Category, string Name, string Hex, string Hint)[]
        {
            (FileCategory.Videos, "Videos", "#9C84C7", "Eigene Dateien. Ohne Sicherung sind sie nach dem Löschen unwiederbringlich weg."),
            (FileCategory.Images, "Bilder", "#D887AE", "Eigene Dateien. Ohne Sicherung sind sie nach dem Löschen unwiederbringlich weg."),
            (FileCategory.Audio, "Audio", "#7C9EDD", "Eigene Dateien. Ohne Sicherung sind sie nach dem Löschen unwiederbringlich weg."),
            (FileCategory.Documents, "Dokumente", "#D2BD70", "Eigene Dateien. Ohne Sicherung sind sie nach dem Löschen unwiederbringlich weg."),
            (FileCategory.Archives, "Archive", "#D89C67", "Oft Downloads oder Sicherungen. Vor dem Löschen prüfen, ob der Inhalt noch irgendwo anders vorliegt."),
            (FileCategory.Installers, "Installer und Abbilder", "#C87A70", "Meist erneut herunterladbar. Bereits installierte Programme laufen auch ohne ihre Installer weiter."),
            (FileCategory.AiModels, "KI-Modelle", "#65BF9F", "Programme, die ein Modell nutzen, brauchen es weiter. Nach dem Löschen muss es neu geladen oder neu erzeugt werden."),
            (FileCategory.VirtualDisks, "Virtuelle Datenträger", "#42A3A8", "Enthalten ganze Systeme oder Daten (WSL, Docker, virtuelle Maschinen). Ohne die Datei ist deren Inhalt verloren. Sie wachsen von selbst, schrumpfen aber nicht: Erst im Inneren aufräumen (z. B. docker system prune), dann die Datei verkleinern (WSL beenden mit wsl --shutdown, dann in PowerShell Optimize-VHD oder in diskpart compact vdisk). Cleanii führt das nicht aus."),
            (FileCategory.Apps, "Apps und Programme", "#75C8E4", "Programme laufen nur mit diesen Dateien. Zum Entfernen die Programmliste von Windows nutzen statt Ordner zu löschen."),
            (FileCategory.TempAndCaches, "Temp und Caches", "#809B4C", "Wird bei Bedarf neu angelegt. Das nächste Starten der betroffenen Programme kann etwas länger dauern, z. B. bei Shader-Caches von Spielen."),
            (FileCategory.OldWindows, "Alte Windows-Installation", "#956298", "Erlaubt die Rückkehr zur früheren Windows-Version. Windows entfernt den Ordner nach einiger Zeit von selbst."),
            (FileCategory.System, "System", "#586B80", "Gehört zu Windows (Auslagerungs- und Ruhezustandsdatei, Systemordner). Nicht von Hand löschen."),
            (FileCategory.RecycleBin, "Papierkorb", "#7F6342", "Gelöschte Dateien, die sich noch wiederherstellen lassen. Nach dem Leeren sind sie endgültig weg."),
            (FileCategory.Other, "Sonstiges", "#8B9095", "Nicht zugeordnet. Den Inhalt prüfen, bevor etwas entfernt wird."),
        };

        if (entries.Length != Enum.GetValues<FileCategory>().Length)
        {
            throw new InvalidOperationException("CategoryCatalog muss jede FileCategory enthalten.");
        }

        var result = new Dictionary<FileCategory, CategoryInfo>();
        foreach (var (category, name, hex, hint) in entries)
        {
            result[category] = new CategoryInfo(category, name, CreateBrush(hex), hint);
        }

        return result;
    }
}
