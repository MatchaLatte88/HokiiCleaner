using System.Globalization;
using System.Windows.Media;
using Cleanii.App.Infrastructure;
using Cleanii.Core;
using Cleanii.Core.Scanning;

namespace Cleanii.App.ViewModels;

public enum RowKind
{
    Folder,
    File,

    /// <summary>Sammelzeile für die Dateien, die unterhalb der Anzeigegrenze liegen.</summary>
    MoreFiles,
}

/// <summary>Eine Zeile der Orte-Ansicht: ein Unterordner, eine einzelne Datei oder die Sammelzeile für kleinere Dateien.</summary>
public sealed class FolderRowViewModel : ObservableObject
{
    private static readonly Geometry FolderIcon = Frozen("M2,6 A2,2 0 0 1 4,4 H9 L11,6.5 H20 A2,2 0 0 1 22,8.5 V18 A2,2 0 0 1 20,20 H4 A2,2 0 0 1 2,18 Z");
    private static readonly Geometry FileIcon = Frozen("M7,2 H14 L19,7 V20 A2,2 0 0 1 17,22 H7 A2,2 0 0 1 5,20 V4 A2,2 0 0 1 7,2 Z");

    private bool _isHovered;
    private bool _isMarked;
    private BasketViewModel? _basket;

    /// <param name="node">Der Ordner; nur bei <see cref="RowKind.Folder"/>.</param>
    /// <param name="fullPath">Pfad des Ordners oder der Datei; bei der Sammelzeile der aktuelle Ordner.</param>
    /// <param name="bytes">Angezeigte Belegung (bei aktivem Typ-Filter nur die des Typs).</param>
    /// <param name="detail">Zusatz hinter dem Anteil (z. B. Dateizahl oder Typ); <c>null</c> = nur der Anteil.</param>
    /// <param name="info">Beschreibung für die Detailanzeige des gewählten Eintrags.</param>
    public FolderRowViewModel(
        RowKind kind,
        DirectoryNode? node,
        string name,
        string fullPath,
        long bytes,
        long shareBase,
        Brush brush,
        IReadOnlyList<long> sizeByCategory,
        string? detail,
        string info,
        string? trail = null)
    {
        Kind = kind;
        Node = node;
        Name = name;
        FullPath = fullPath;
        Bytes = bytes;
        Brush = brush;
        SizeByCategory = sizeByCategory;
        InfoText = info;
        TrailText = trail;
        SizeText = bytes > 0 ? ByteFormat.Format(bytes) : "–";

        double share = shareBase <= 0 ? 0 : (double)bytes / shareBase;
        ShareValue = share * 100;
        ShareText = share.ToString("P1", CultureInfo.CurrentCulture);
        DetailText = detail is null ? ShareText : $"{ShareText} · {detail}";

        StateText = node?.State switch
        {
            DirectoryState.Unreadable => "nicht lesbar",
            DirectoryState.LinkSkipped => "Verknüpfung, nicht durchsucht",
            _ => null,
        };
    }

    public RowKind Kind { get; }

    /// <summary>Dateigröße (logisch) des Eintrags, für den Export.</summary>
    public long LogicalBytes { get; init; }

    /// <summary>Dateizahl bei Ordnern und der Sammelzeile; <c>null</c> bei einzelnen Dateien.</summary>
    public long? FileCount { get; init; }

    /// <summary>Anzeigename der Kategorie (Datei) bzw. der vorherrschenden Kategorie (Ordner); leer, wenn unbekannt.</summary>
    public string CategoryName { get; init; } = string.Empty;

    /// <summary>
    /// Letzte Änderung der Datei bzw. jüngste Änderung im Ordner (UTC); <see cref="DateTime.MinValue"/>, wenn unbekannt.
    /// </summary>
    public DateTime ModifiedUtc { get; init; }

    /// <summary>Der Ordner, in dem der Eintrag liegt; bei Suchtreffern ein anderer als der angezeigte.</summary>
    public DirectoryNode? ContainingFolder { get; init; }

    public string TypeText => Kind switch
    {
        RowKind.Folder => "Ordner",
        RowKind.File => "Datei",
        _ => "Kleinere Dateien",
    };

    /// <summary>Der Ordner; <c>null</c> bei Dateien und der Sammelzeile.</summary>
    public DirectoryNode? Node { get; }

    public string Name { get; }

    /// <summary>Pfad des Eintrags (Ordner oder Datei); bei der Sammelzeile der aktuelle Ordner.</summary>
    public string FullPath { get; }

    /// <summary>Angezeigte Belegung (bei aktivem Typ-Filter nur die des Typs).</summary>
    public long Bytes { get; }

    public string SizeText { get; }

    public double ShareValue { get; }

    public string ShareText { get; }

    /// <summary>Zweite Zeile: Anteil, ohne Filter zusätzlich Dateizahl (Ordner) oder Typ (Datei).</summary>
    public string DetailText { get; }

    /// <summary>Für die Detailanzeige: Belegung, Dateigröße, Inhalt bzw. Fehlergrund.</summary>
    public string InfoText { get; }

    /// <summary>Spur zum größten Inhalt eines Ordners, z. B. "↳ AppData › Local › pip · 98 GB"; sonst <c>null</c>.</summary>
    public string? TrailText { get; }

    public bool HasTrailText => TrailText is not null;

    /// <summary>Farbe der Kategorie (Datei) bzw. der vorherrschenden Kategorie (Ordner).</summary>
    public Brush Brush { get; }

    public Geometry Icon => Kind == RowKind.Folder ? FolderIcon : FileIcon;

    /// <summary>Weniger deckend für die Sammelzeile, damit sie nicht wie ein echter Eintrag wirkt.</summary>
    public double IconOpacity => Kind == RowKind.MoreFiles ? 0.5 : 1;

    /// <summary>Belegung des Eintrags je Kategorie (ungefiltert), indiziert mit <c>(int)FileCategory</c>.</summary>
    public IReadOnlyList<long> SizeByCategory { get; }

    /// <summary>Hinweis bei nicht lesbaren oder übersprungenen Ordnern; sonst <c>null</c>.</summary>
    public string? StateText { get; }

    public bool HasStateText => StateText is not null;

    /// <summary>Ordner betreten bzw. Datei im Explorer zeigen. Verknüpfungen werden nicht betreten.</summary>
    public bool CanOpen => Kind switch
    {
        RowKind.Folder => Node!.State != DirectoryState.LinkSkipped,
        RowKind.File => true,
        _ => false,
    };

    /// <summary>Ordner und Dateien lassen sich im Explorer zeigen und ihr Pfad kopieren.</summary>
    public bool CanReveal => Kind != RowKind.MoreFiles;

    /// <summary>Stabile Kennung über einen Neuaufbau der Liste hinweg; <c>null</c> bei der Sammelzeile.</summary>
    public string? Key => Kind == RowKind.MoreFiles ? null : FullPath;

    public bool IsHovered
    {
        get => _isHovered;
        set => Set(ref _isHovered, value);
    }

    /// <summary>Verbindet die Zeile mit dem Auswahlkorb (Haken links). Die Sammelzeile und nicht lesbare Ordner lassen sich nicht markieren.</summary>
    public void AttachBasket(BasketViewModel basket)
    {
        _basket = basket;
        bool markable = Kind == RowKind.File || (Kind == RowKind.Folder && Node!.State == DirectoryState.Scanned);
        string? why = markable ? basket.WhyNotMarkable(FullPath) : null;
        CanMark = markable && why is null;
        MarkToolTip = CanMark
            ? "Für den Papierkorb vormerken (verschoben wird erst nach Bestätigung)"
            : why ?? "Nicht markierbar";
        _isMarked = CanMark && basket.Contains(FullPath);
    }

    /// <summary>Zeile bietet einen Haken an (nicht die Sammelzeile).</summary>
    public bool ShowMark => Kind != RowKind.MoreFiles;

    public bool CanMark { get; private set; }

    public string MarkToolTip { get; private set; } = string.Empty;

    /// <summary>Für den Papierkorb vorgemerkt. Schlägt das Vormerken fehl (z. B. geschützt), springt der Haken zurück.</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (_basket is null || !CanMark || value == _isMarked)
            {
                Raise();
                return;
            }

            bool inBasket = value ? _basket.Add(Entry()) : !_basket.Remove(FullPath);
            _isMarked = inBasket;
            Raise();
        }
    }

    /// <summary>Gleicht den Haken mit dem Korb ab, wenn sich die Auswahl woanders geändert hat.</summary>
    public void RefreshMark()
    {
        bool marked = CanMark && _basket is not null && _basket.Contains(FullPath);
        if (marked != _isMarked)
        {
            _isMarked = marked;
            Raise(nameof(IsMarked));
        }
    }

    private BasketEntry Entry() => Kind == RowKind.Folder
        ? new BasketEntry(FullPath, true, Node!.TotalSizeOnDisk, Node.TotalFileCount, "Orte")
        : new BasketEntry(FullPath, false, Bytes, 1, "Orte");

    private static Geometry Frozen(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
