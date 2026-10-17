using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using HokiiCleaner.App.Controls;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.App.Presentation;
using HokiiCleaner.Core;
using HokiiCleaner.Core.Categorization;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.App.ViewModels;

/// <summary>Typ-Chip über dem Ring: hebt einen Typ hervor.</summary>
public sealed class TypeChipViewModel : ObservableObject
{
    private bool _isOn;

    public TypeChipViewModel(FileCategory category)
    {
        Category = category;
        Name = CategoryCatalog.Get(category).Name;
        Brush = CategoryCatalog.Get(category).Brush;
    }

    public FileCategory Category { get; }

    public string Name { get; }

    public Brush Brush { get; }

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }
}

/// <summary>
/// Sonar: bereitet die Zusammensetzung des aktuellen Ordners für den Ring auf (Abschnitte je Ordner, Typen je Abschnitt),
/// verwaltet Hover, Auswahl und Typ-Hervorhebung und liefert die Texte der Mitte und der Info-Karte.
/// </summary>
public sealed class SonarViewModel : ObservableObject
{
    /// <summary>Schlüssel für die Dateien, die direkt im aktuellen Ordner liegen.</summary>
    public static readonly object OwnFilesKey = new();

    private const int MaxSegments = 48;
    private const int TopFileCount = 3;
    private const int MaxMixItems = 6;
    private const int MaxChips = 8;

    private sealed record Entry(object Key, string Name, string Path, DirectoryNode? Node, long Bytes, IReadOnlyList<long> Sizes, long Files);

    private readonly PlacesViewModel _places;
    private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();
    private int _topFilesVersion;

    private IReadOnlyList<DialSegment> _segments = Array.Empty<DialSegment>();
    private long _totalBytes;
    private long _unattributedBytes;
    private double _scanFraction = -1;
    private object? _selectedKey;
    private object? _hoveredKey;
    private FileCategory? _focusCategory;
    private IReadOnlyList<TypeChipViewModel> _chips = Array.Empty<TypeChipViewModel>();

    private string _readoutBig = string.Empty;
    private string _readoutLabel = string.Empty;
    private string _readoutSub = string.Empty;

    private bool _hasSelection;
    private string _selName = string.Empty;
    private string _selPath = string.Empty;
    private string _selSizeNumber = string.Empty;
    private string _selSizeSuffix = string.Empty;
    private long _selMixTotal;
    private IReadOnlyList<BarSegment> _selMixSegments = Array.Empty<BarSegment>();
    private IReadOnlyList<CompositionItemViewModel> _selMixItems = Array.Empty<CompositionItemViewModel>();
    private IReadOnlyList<FileRowViewModel> _selTopFiles = Array.Empty<FileRowViewModel>();
    private string _noticeText = string.Empty;

    public SonarViewModel(PlacesViewModel places)
    {
        _places = places;
        _places.CompositionChanged += (_, _) => Rebuild();
        _places.Cleared += (_, _) => Clear();

        OpenCommand = new RelayCommand<object>(Open, key => key is DirectoryNode);
        OpenSelectedCommand = new RelayCommand(() => Open(SelectedKey!), () => SelectedKey is DirectoryNode);
        RevealSelectedCommand = new RelayCommand(RevealSelected, () => HasSelection);
        ToggleFocusCommand = new RelayCommand<TypeChipViewModel>(ToggleFocus);
    }

    public ICommand OpenCommand { get; }

    public ICommand OpenSelectedCommand { get; }

    public ICommand RevealSelectedCommand { get; }

    public ICommand ToggleFocusCommand { get; }

    public PlacesViewModel Places => _places;

    public IReadOnlyList<DialSegment> Segments
    {
        get => _segments;
        private set => Set(ref _segments, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        private set => Set(ref _totalBytes, value);
    }

    public long UnattributedBytes
    {
        get => _unattributedBytes;
        private set => Set(ref _unattributedBytes, value);
    }

    /// <summary>Scan-Fortschritt 0..1 (negativ = keiner); wird vom Hauptmodell gesetzt.</summary>
    public double ScanFraction
    {
        get => _scanFraction;
        set => Set(ref _scanFraction, value);
    }

    public object? SelectedKey
    {
        get => _selectedKey;
        set
        {
            if (Set(ref _selectedKey, value))
            {
                UpdateInspector();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public object? HoveredKey
    {
        get => _hoveredKey;
        set
        {
            if (Set(ref _hoveredKey, value))
            {
                UpdateReadout();
            }
        }
    }

    public FileCategory? FocusCategory
    {
        get => _focusCategory;
        private set
        {
            if (Set(ref _focusCategory, value))
            {
                UpdateReadout();
            }
        }
    }

    public IReadOnlyList<TypeChipViewModel> Chips
    {
        get => _chips;
        private set => Set(ref _chips, value);
    }

    public string ReadoutBig
    {
        get => _readoutBig;
        private set => Set(ref _readoutBig, value);
    }

    public string ReadoutLabel
    {
        get => _readoutLabel;
        private set => Set(ref _readoutLabel, value);
    }

    public string ReadoutSub
    {
        get => _readoutSub;
        private set => Set(ref _readoutSub, value);
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => Set(ref _hasSelection, value);
    }

    public string SelName
    {
        get => _selName;
        private set => Set(ref _selName, value);
    }

    public string SelPath
    {
        get => _selPath;
        private set => Set(ref _selPath, value);
    }

    public string SelSizeNumber
    {
        get => _selSizeNumber;
        private set => Set(ref _selSizeNumber, value);
    }

    public string SelSizeSuffix
    {
        get => _selSizeSuffix;
        private set => Set(ref _selSizeSuffix, value);
    }

    public long SelMixTotal
    {
        get => _selMixTotal;
        private set => Set(ref _selMixTotal, value);
    }

    public IReadOnlyList<BarSegment> SelMixSegments
    {
        get => _selMixSegments;
        private set => Set(ref _selMixSegments, value);
    }

    public IReadOnlyList<CompositionItemViewModel> SelMixItems
    {
        get => _selMixItems;
        private set => Set(ref _selMixItems, value);
    }

    public IReadOnlyList<FileRowViewModel> SelTopFiles
    {
        get => _selTopFiles;
        private set
        {
            if (Set(ref _selTopFiles, value))
            {
                Raise(nameof(HasTopFiles));
            }
        }
    }

    public bool HasTopFiles => SelTopFiles.Count > 0;

    /// <summary>Hinweis, z. B. wenn der Explorer nicht geöffnet werden konnte; leer = keiner.</summary>
    public string NoticeText
    {
        get => _noticeText;
        private set
        {
            if (Set(ref _noticeText, value))
            {
                Raise(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => NoticeText.Length > 0;

    private void Rebuild()
    {
        FolderComposition? composition = _places.Composition;
        if (composition is null)
        {
            Clear();
            return;
        }

        var entries = new List<Entry>();
        foreach (ChildComposition child in composition.Children)
        {
            if (child.TotalSizeOnDisk > 0)
            {
                entries.Add(new Entry(child.Node, child.Node.Name, child.Node.FullPath, child.Node, child.TotalSizeOnDisk, child.SizeByCategory, child.Node.TotalFileCount));
            }
        }

        long own = composition.OwnFilesSizeByCategory.Sum();
        if (own > 0)
        {
            entries.Add(new Entry(OwnFilesKey, "Dateien in diesem Ordner", composition.Folder.FullPath, null, own, composition.OwnFilesSizeByCategory, composition.Folder.Files.Length));
        }

        entries.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        _entries = entries;

        var segments = entries.Take(MaxSegments).Select(ToSegment).ToList();
        if (entries.Count > MaxSegments)
        {
            // Der Rest wird zu einem Abschnitt ohne Auswahl zusammengefasst.
            var rest = entries.Skip(MaxSegments).ToArray();
            var sizes = new long[composition.SizeByCategory.Count];
            foreach (Entry entry in rest)
            {
                for (int i = 0; i < sizes.Length; i++)
                {
                    sizes[i] += entry.Sizes[i];
                }
            }

            segments.Add(new DialSegment(null, rest.Sum(r => r.Bytes), CategoryCatalog.UnattributedBrush, ToParts(sizes)));
        }

        DriveContext? drive = _places.IsAtRoot ? _places.Drive : null;
        TotalBytes = drive?.TotalBytes ?? composition.TotalSizeOnDisk;
        UnattributedBytes = drive is null ? 0 : Math.Max(0, drive.UsedBytes - composition.TotalSizeOnDisk);
        Segments = segments;

        Chips = composition.SizeByCategory
            .Select((bytes, index) => (Category: (FileCategory)index, Bytes: bytes))
            .Where(c => c.Bytes > 0)
            .OrderByDescending(c => c.Bytes)
            .Take(MaxChips)
            .Select(c => new TypeChipViewModel(c.Category) { IsOn = c.Category == FocusCategory })
            .ToArray();
        if (FocusCategory is FileCategory focus && Chips.All(c => c.Category != focus))
        {
            FocusCategory = null;
        }

        // Die Auswahl des vorherigen Ordners gilt nicht mehr: größter Abschnitt vorwählen.
        _selectedKey = null;
        SelectedKey = entries.Count > 0 ? entries[0].Key : null;
        UpdateReadout();
    }

    private static DialSegment ToSegment(Entry entry)
    {
        IReadOnlyList<DialPart> parts = ToParts(entry.Sizes);
        Brush fill = parts.Count > 0 ? parts[0].Fill : CategoryCatalog.UnattributedBrush;
        return new DialSegment(entry.Key, entry.Bytes, fill, parts);
    }

    private static IReadOnlyList<DialPart> ToParts(IReadOnlyList<long> sizes)
        => sizes
            .Select((bytes, index) => (Category: (FileCategory)index, Bytes: bytes))
            .Where(p => p.Bytes > 0)
            .OrderByDescending(p => p.Bytes)
            .Select(p => new DialPart(p.Category, p.Bytes, CategoryCatalog.Get(p.Category).Brush))
            .ToArray();

    private void Clear()
    {
        _entries = Array.Empty<Entry>();
        Segments = Array.Empty<DialSegment>();
        Chips = Array.Empty<TypeChipViewModel>();
        TotalBytes = 0;
        UnattributedBytes = 0;
        FocusCategory = null;
        _selectedKey = null;
        Raise(nameof(SelectedKey));
        HoveredKey = null;
        UpdateInspector();
        UpdateReadout();
    }

    private Entry? Find(object? key) => key is null ? null : _entries.FirstOrDefault(e => ReferenceEquals(e.Key, key));

    private void ToggleFocus(TypeChipViewModel chip)
    {
        FocusCategory = FocusCategory == chip.Category ? null : chip.Category;
        foreach (TypeChipViewModel other in Chips)
        {
            other.IsOn = other.Category == FocusCategory;
        }
    }

    private void Open(object key)
    {
        if (key is DirectoryNode node)
        {
            _places.OpenFolder(node);
        }
    }

    private void RevealSelected()
    {
        if (Find(SelectedKey) is not Entry entry)
        {
            return;
        }

        try
        {
            ExplorerLauncher.Reveal(entry.Path);
            NoticeText = string.Empty;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            NoticeText = $"Explorer konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    private void UpdateReadout()
    {
        FolderComposition? composition = _places.Composition;
        if (composition is null)
        {
            ReadoutBig = string.Empty;
            ReadoutLabel = string.Empty;
            ReadoutSub = string.Empty;
            return;
        }

        DriveContext? drive = _places.IsAtRoot ? _places.Drive : null;
        string basis = drive is null ? "DES ORDNERS" : "DER KAPAZITÄT";
        string folderName = composition.Folder.Name;

        if (Find(HoveredKey) is Entry hovered)
        {
            (string number, string unit) = ByteFormat.Split(hovered.Bytes);
            ReadoutBig = number;
            ReadoutLabel = $"{unit} · {hovered.Name}";
            ReadoutSub = (Share(hovered.Bytes)).ToString("P1", CultureInfo.CurrentCulture) + " " + basis;
        }
        else if (FocusCategory is FileCategory focus)
        {
            (string number, string unit) = ByteFormat.Split(composition.SizeByCategory[(int)focus]);
            ReadoutBig = number;
            ReadoutLabel = $"{unit} · {CategoryCatalog.Get(focus).Name}";
            ReadoutSub = "IN " + folderName.ToUpperInvariant();
        }
        else if (drive is not null)
        {
            (string number, string unit) = ByteFormat.Split(drive.UsedBytes);
            double free = drive.TotalBytes <= 0 ? 0 : 1.0 - (double)drive.UsedBytes / drive.TotalBytes;
            ReadoutBig = number;
            ReadoutLabel = $"{unit} belegt von {ByteFormat.Format(drive.TotalBytes)}";
            ReadoutSub = free.ToString("P0", CultureInfo.CurrentCulture) + " FREI";
        }
        else
        {
            (string number, string unit) = ByteFormat.Split(composition.TotalSizeOnDisk);
            ReadoutBig = number;
            ReadoutLabel = $"{unit} in {folderName}";
            ReadoutSub = composition.Folder.TotalFileCount.ToString("N0", CultureInfo.CurrentCulture) + " DATEIEN";
        }
    }

    private double Share(long bytes) => TotalBytes <= 0 ? 0 : (double)bytes / TotalBytes;

    private void UpdateInspector()
    {
        if (Find(SelectedKey) is not Entry entry)
        {
            HasSelection = false;
            SelName = string.Empty;
            SelPath = string.Empty;
            SelSizeNumber = string.Empty;
            SelSizeSuffix = string.Empty;
            SelMixTotal = 0;
            SelMixSegments = Array.Empty<BarSegment>();
            SelMixItems = Array.Empty<CompositionItemViewModel>();
            SelTopFiles = Array.Empty<FileRowViewModel>();
            _topFilesVersion++;
            return;
        }

        bool drive = _places.IsAtRoot && _places.Drive is not null;
        (string number, string unit) = ByteFormat.Split(entry.Bytes);
        HasSelection = true;
        SelName = entry.Name;
        SelPath = entry.Path;
        SelSizeNumber = number;
        SelSizeSuffix = $"{unit} · {Share(entry.Bytes).ToString("P1", CultureInfo.CurrentCulture)} {(drive ? "der Kapazität" : "des Ordners")}";

        long total = entry.Sizes.Sum();
        SelMixTotal = total;
        SelMixSegments = ToParts(entry.Sizes).Select(p => new BarSegment(p.Category, p.Bytes, p.Fill)).ToArray();
        SelMixItems = ToParts(entry.Sizes)
            .Take(MaxMixItems)
            .Select(p => new CompositionItemViewModel(
                CategoryCatalog.Get(p.Category).Name,
                p.Fill,
                ByteFormat.Format(p.Bytes),
                ((double)p.Bytes / total).ToString("P0", CultureInfo.CurrentCulture)))
            .ToArray();

        _ = LoadTopFilesAsync(entry);
    }

    private async Task LoadTopFilesAsync(Entry entry)
    {
        int version = ++_topFilesVersion;
        FolderComposition? composition = _places.Composition;
        if (composition is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<TopFile> files = await Task.Run(() => entry.Node is not null
                ? FileRanking.Largest(entry.Node, TopFileCount)
                : composition.Folder.Files
                    .Where(f => !f.IsHardLinkDuplicate)
                    .OrderByDescending(f => f.SizeOnDisk)
                    .Take(TopFileCount)
                    .Select(f => new TopFile(composition.Folder, f))
                    .ToArray());

            if (version == _topFilesVersion)
            {
                SelTopFiles = files.Select(f => new FileRowViewModel(f)).ToArray();
            }
        }
        catch (Exception ex)
        {
            if (version == _topFilesVersion)
            {
                NoticeText = $"Größte Dateien konnten nicht ermittelt werden: {ex.Message}";
            }
        }
    }
}
