using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cleanii.App.Controls;
using Cleanii.App.Infrastructure;
using Cleanii.App.Presentation;
using Cleanii.Core;
using Cleanii.Core.Categorization;
using Cleanii.Core.Scanning;

namespace Cleanii.App.ViewModels;

/// <summary>Größe und Belegung des Laufwerks, wenn der Scan ein ganzes Laufwerk umfasst.</summary>
public sealed record DriveContext(long TotalBytes, long UsedBytes);

public enum PlacesSort
{
    Size,
    Name,
    Modified,
}

/// <summary>
/// Orte-Ansicht: Ordnerliste und Treemap eines Ordners, Navigation per Breadcrumb und optionaler Filter auf einen Typ.
/// Hält außerdem die Zusammensetzung des aktuellen Ordners, auf der auch der Sonar-Ring aufbaut.
/// </summary>
public sealed class PlacesViewModel : ObservableObject
{
    private const int MaxTreemapItems = 60;
    private const int MaxCompositionItems = 6;

    /// <summary>Einzeln gelistete Dateien je Ordner (die größten); der Rest erscheint als eine Sammelzeile.</summary>
    private const int MaxFileRows = 500;

    /// <summary>Kürzeste Suche; ein einzelner Buchstabe träfe fast alles.</summary>
    private const int MinSearchLength = 2;

    private const int MaxSearchRows = 500;

    /// <summary>Ab diesem Alter gilt eine Datei oder ein Ordner als "lange nicht geändert".</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(365);

    private static readonly int CategoryCount = Enum.GetValues<FileCategory>().Length;
    private static readonly Brush BucketBrush = CreateBucketBrush();

    private readonly BasketViewModel _basket;
    private PlacesSort _sort = PlacesSort.Size;
    private bool _onlyStale;
    private string _searchText = string.Empty;
    private SearchResult? _searchResult;
    private CancellationTokenSource? _searchCts;
    private bool _searching;
    private string? _pendingSelectKey;
    private DirectoryNode? _root;
    private DriveContext? _drive;
    private DirectoryNode? _current;
    private FolderComposition? _composition;
    private int _version;
    private bool _isBusy;
    private string _errorText = string.Empty;
    private bool _filterByType;
    private FileCategory? _focusCategory;
    private IReadOnlyList<CrumbViewModel> _crumbs = Array.Empty<CrumbViewModel>();
    private IReadOnlyList<FolderRowViewModel> _rows = Array.Empty<FolderRowViewModel>();
    private IReadOnlyList<TreemapItem> _treemapItems = Array.Empty<TreemapItem>();
    private FolderRowViewModel? _selectedRow;
    private FolderRowViewModel? _hoveredRow;
    private string _headerText = string.Empty;
    private string _compositionTitle = string.Empty;
    private long _compositionTotal;
    private IReadOnlyList<BarSegment> _compositionSegments = Array.Empty<BarSegment>();
    private IReadOnlyList<CompositionItemViewModel> _compositionItems = Array.Empty<CompositionItemViewModel>();

    public PlacesViewModel(BasketViewModel basket)
    {
        _basket = basket;
        _basket.Changed += (_, _) => RefreshMarks();
        OpenCommand = new RelayCommand<FolderRowViewModel>(Open, row => row.CanOpen);
        NavigateCommand = new RelayCommand<CrumbViewModel>(crumb => _ = ShowFolderAsync(crumb.Node));
        UpCommand = new RelayCommand(Up, () => CanGoUp);
        RevealCommand = new RelayCommand(() => Reveal(SelectedRow!), () => SelectedRow?.CanReveal == true);
        CopyPathCommand = new RelayCommand(CopyPath, () => SelectedRow?.CanReveal == true);
        ExportCommand = new RelayCommand(Export, () => Rows.Count > 0);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => IsSearching && SelectedRow?.ContainingFolder is not null);
    }

    /// <summary>Meldung für die Statuszeile (z. B. Export gespeichert oder fehlgeschlagen).</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>Speichert die aktuelle Liste (Ordner, Dateien oder Suchtreffer) als CSV.</summary>
    public ICommand ExportCommand { get; }

    /// <summary>Wechselt von einem Suchtreffer in den Ordner, in dem er liegt, und wählt ihn dort.</summary>
    public ICommand ShowInFolderCommand { get; }

    public PlacesSort Sort
    {
        get => _sort;
        set
        {
            if (Set(ref _sort, value))
            {
                Raise(nameof(SortBySize));
                Raise(nameof(SortByName));
                Raise(nameof(SortByModified));
                RebuildRows();
            }
        }
    }

    public bool SortBySize
    {
        get => Sort == PlacesSort.Size;
        set => SetSort(value, PlacesSort.Size);
    }

    public bool SortByName
    {
        get => Sort == PlacesSort.Name;
        set => SetSort(value, PlacesSort.Name);
    }

    public bool SortByModified
    {
        get => Sort == PlacesSort.Modified;
        set => SetSort(value, PlacesSort.Modified);
    }

    private void SetSort(bool selected, PlacesSort sort)
    {
        if (selected)
        {
            Sort = sort;
        }
    }

    /// <summary>Zeigt nur Einträge, in denen seit über einem Jahr nichts geändert wurde ("groß und alt").</summary>
    public bool OnlyStale
    {
        get => _onlyStale;
        set
        {
            if (Set(ref _onlyStale, value))
            {
                RebuildRows();
            }
        }
    }

    /// <summary>Suchtext für Namen im aktuellen Ordner und darunter; ab zwei Zeichen werden Treffer statt der Ordnerliste gezeigt.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? string.Empty))
            {
                return;
            }

            Raise(nameof(IsSearching));
            _ = RunSearchAsync();
        }
    }

    public bool IsSearching => _searchText.Trim().Length >= MinSearchLength;

    /// <summary>Ordner betreten bzw. Datei im Explorer zeigen (Doppelklick, Enter).</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Zeigt den gewählten Ordner oder die gewählte Datei im Explorer. Verändert nichts.</summary>
    public ICommand RevealCommand { get; }

    /// <summary>Kopiert den Pfad des gewählten Eintrags in die Zwischenablage.</summary>
    public ICommand CopyPathCommand { get; }

    public ICommand NavigateCommand { get; }

    public ICommand UpCommand { get; }

    /// <summary>True, solange die Zusammensetzung des aktuellen Ordners berechnet oder gesucht wird.</summary>
    public bool IsBusy
    {
        get => _isBusy || _searching;
        private set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                Raise();
            }
        }
    }

    private bool IsSearchBusy
    {
        set
        {
            if (_searching != value)
            {
                _searching = value;
                Raise(nameof(IsBusy));
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value))
            {
                Raise(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorText.Length > 0;

    public DirectoryNode? CurrentFolder => _current;

    public bool CanGoUp => _current is not null && _current != _root && _current.Parent is not null;

    public IReadOnlyList<CrumbViewModel> Crumbs
    {
        get => _crumbs;
        private set => Set(ref _crumbs, value);
    }

    public IReadOnlyList<FolderRowViewModel> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public IReadOnlyList<TreemapItem> TreemapItems
    {
        get => _treemapItems;
        private set => Set(ref _treemapItems, value);
    }

    public string HeaderText
    {
        get => _headerText;
        private set => Set(ref _headerText, value);
    }

    /// <summary>Typ, der in der Hauptansicht gewählt ist; Grundlage des Filters.</summary>
    public FileCategory? FocusCategory
    {
        get => _focusCategory;
        set
        {
            if (!Set(ref _focusCategory, value))
            {
                return;
            }

            Raise(nameof(CanFilter));
            Raise(nameof(FilterLabel));
            if (FilterByType)
            {
                RebuildRows();
            }
        }
    }

    public bool CanFilter => FocusCategory is not null;

    public string FilterLabel => FocusCategory is FileCategory category
        ? $"Nur {CategoryCatalog.Get(category).Name}"
        : "Nach Typ filtern";

    /// <summary>Zeigt nur die Belegung des gewählten Typs.</summary>
    public bool FilterByType
    {
        get => _filterByType;
        set
        {
            if (Set(ref _filterByType, value))
            {
                RebuildRows();
            }
        }
    }

    public FolderRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                Raise(nameof(HasSelection));
                Raise(nameof(SelectionTitle));
                Raise(nameof(SelectedPath));
                Raise(nameof(SelectedInfo));
                RebuildComposition();
            }
        }
    }

    public bool HasSelection => SelectedRow is not null;

    public string SelectionTitle => SelectedRow?.Kind switch
    {
        RowKind.Folder => "AUSGEWÄHLTER ORDNER",
        RowKind.File => "AUSGEWÄHLTE DATEI",
        RowKind.MoreFiles => "KLEINERE DATEIEN",
        _ => string.Empty,
    };

    public string SelectedPath => SelectedRow?.FullPath ?? string.Empty;

    public string SelectedInfo => SelectedRow?.InfoText ?? string.Empty;

    public FolderRowViewModel? HoveredRow
    {
        get => _hoveredRow;
        set
        {
            if (!Set(ref _hoveredRow, value))
            {
                return;
            }

            foreach (FolderRowViewModel row in Rows)
            {
                row.IsHovered = row == value;
            }
        }
    }

    public string CompositionTitle
    {
        get => _compositionTitle;
        private set => Set(ref _compositionTitle, value);
    }

    public long CompositionTotal
    {
        get => _compositionTotal;
        private set => Set(ref _compositionTotal, value);
    }

    public IReadOnlyList<BarSegment> CompositionSegments
    {
        get => _compositionSegments;
        private set => Set(ref _compositionSegments, value);
    }

    public IReadOnlyList<CompositionItemViewModel> CompositionItems
    {
        get => _compositionItems;
        private set => Set(ref _compositionItems, value);
    }

    /// <summary>Zusammensetzung des aktuellen Ordners; <c>null</c> solange sie berechnet wird oder kein Scan vorliegt.</summary>
    public FolderComposition? Composition => _composition;

    /// <summary>Laufwerksangaben, wenn die Scan-Wurzel ein ganzes Laufwerk ist; sonst <c>null</c>.</summary>
    public DriveContext? Drive => _drive;

    public bool IsAtRoot => _current is not null && _current == _root;

    /// <summary>Wird ausgelöst, wenn die Zusammensetzung eines Ordners fertig berechnet ist.</summary>
    public event EventHandler? CompositionChanged;

    /// <summary>Wird ausgelöst, wenn die Ergebnisse eines früheren Scans verworfen werden.</summary>
    public event EventHandler? Cleared;

    /// <summary>Beginnt bei der Scan-Wurzel.</summary>
    public void Load(DirectoryNode root, DriveContext? drive = null)
    {
        _root = root;
        _drive = drive;
        _ = ShowFolderAsync(root);
    }

    /// <summary>Öffnet einen Ordner aus dem Scan, z. B. aus dem Sonar-Ring.</summary>
    public void OpenFolder(DirectoryNode folder) => _ = ShowFolderAsync(folder);

    /// <summary>
    /// Baut die Ansicht neu auf, nachdem sich der Scan-Baum geändert hat (z. B. nach dem Verschieben in den Papierkorb).
    /// Ist der gezeigte Ordner nicht mehr da, wird der nächste vorhandene darüber gezeigt.
    /// </summary>
    public void Refresh()
    {
        if (_root is null)
        {
            return;
        }

        DirectoryNode target = _current ?? _root;
        while (target != _root && !ScanTreeEditor.IsAttached(_root, target))
        {
            target = target.Parent ?? _root;
        }

        _pendingSelectKey = SelectedRow?.Key;
        _ = ShowFolderAsync(target);
    }

    private void RefreshMarks()
    {
        foreach (FolderRowViewModel row in Rows)
        {
            row.RefreshMark();
        }
    }

    /// <summary>Verwirft Ergebnisse eines früheren Scans.</summary>
    public void Clear()
    {
        _version++;
        _root = null;
        _current = null;
        _composition = null;
        ResetSearch();
        _pendingSelectKey = null;
        IsBusy = false;
        ErrorText = string.Empty;
        HoveredRow = null;
        Crumbs = Array.Empty<CrumbViewModel>();
        Rows = Array.Empty<FolderRowViewModel>();
        TreemapItems = Array.Empty<TreemapItem>();
        SelectedRow = null;
        HeaderText = string.Empty;
        _drive = null;
        Raise(nameof(CurrentFolder));
        Raise(nameof(CanGoUp));
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Öffnet den Ordner mit dem angegebenen absoluten Pfad innerhalb des Scans.</summary>
    public void NavigateToPath(string path)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("Es gibt noch kein Scan-Ergebnis.");
        }

        string rootPath = _root.FullPath.TrimEnd('\\');
        string target = Path.GetFullPath(path).TrimEnd('\\');
        if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{path}' liegt nicht im Scan von '{_root.FullPath}'.", nameof(path));
        }

        DirectoryNode node = _root;
        foreach (string segment in target.Substring(rootPath.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Ordner '{segment}' nicht gefunden in '{node.FullPath}'.", nameof(path));
        }

        _ = ShowFolderAsync(node);
    }

    private void Open(FolderRowViewModel row)
    {
        if (row.Kind == RowKind.Folder)
        {
            _ = ShowFolderAsync(row.Node!);
        }
        else if (row.Kind == RowKind.File)
        {
            Reveal(row);
        }
    }

    private void Reveal(FolderRowViewModel row)
    {
        try
        {
            ExplorerLauncher.Reveal(row.FullPath);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ErrorText = $"Explorer konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    private void CopyPath()
    {
        try
        {
            Clipboard.SetText(SelectedRow!.FullPath);
        }
        catch (ExternalException ex)
        {
            // Die Zwischenablage kann kurzzeitig von einem anderen Programm gesperrt sein.
            ErrorText = $"Der Pfad konnte nicht kopiert werden: {ex.Message}";
        }
    }

    private void Up()
    {
        if (_current?.Parent is DirectoryNode parent)
        {
            _ = ShowFolderAsync(parent);
        }
    }

    private async Task ShowFolderAsync(DirectoryNode folder)
    {
        int version = ++_version;
        ResetSearch(); // eine Suche gilt für den Ordner, in dem sie gestartet wurde
        IsBusy = true;
        ErrorText = string.Empty;
        HeaderText = "Berechne …";
        _current = folder;
        Crumbs = BuildCrumbs(folder);
        Raise(nameof(CurrentFolder));
        Raise(nameof(CanGoUp));

        FolderComposition composition;
        try
        {
            composition = await Task.Run(() => CategoryAnalyzer.Compose(folder));
        }
        catch (Exception ex)
        {
            if (version == _version)
            {
                ErrorText = $"Der Ordner konnte nicht ausgewertet werden: {ex.Message}";
                IsBusy = false;
            }

            return;
        }

        if (version != _version)
        {
            return; // ein neuerer Aufruf hat übernommen
        }

        _composition = composition;
        IsBusy = false;
        RebuildRows();
        CompositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<CrumbViewModel> BuildCrumbs(DirectoryNode folder)
    {
        var nodes = new List<DirectoryNode>();
        for (DirectoryNode? node = folder; node is not null; node = node == _root ? null : node.Parent)
        {
            nodes.Add(node);
        }

        nodes.Reverse();
        return nodes.Select((n, i) => new CrumbViewModel(n.Name, n, IsLast: i == nodes.Count - 1)).ToArray();
    }

    private void RebuildRows()
    {
        FolderComposition? composition = _composition;
        if (composition is null)
        {
            return;
        }

        string? keepKey = _pendingSelectKey ?? SelectedRow?.Key;
        _pendingSelectKey = null;
        HoveredRow = null;

        FileCategory? filter = FilterByType ? FocusCategory : null;
        long total = filter is FileCategory f ? composition.SizeByCategory[(int)f] : composition.TotalSizeOnDisk;
        DateTime? cutoff = OnlyStale ? DateTime.UtcNow - StaleAfter : null;

        bool showHits = IsSearching && _searchResult is not null;
        List<FolderRowViewModel> rows = showHits
            ? BuildSearchRows(_searchResult!, composition.Folder, composition.TotalSizeOnDisk, filter, cutoff)
            : BuildFolderRows(composition, total, filter, cutoff);
        foreach (FolderRowViewModel row in rows)
        {
            row.AttachBasket(_basket);
        }

        FolderRowViewModel[] bySize = rows
            .OrderByDescending(r => r.Bytes)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        FolderRowViewModel[] sorted = Order(bySize);

        Rows = sorted;
        TreemapItems = BuildTreemapItems(bySize);
        SelectedRow = (keepKey is null ? null : sorted.FirstOrDefault(r => string.Equals(r.Key, keepKey, StringComparison.OrdinalIgnoreCase)))
            ?? sorted.FirstOrDefault();

        HeaderText = BuildHeader(composition.Folder.Name, total, filter, cutoff is not null, showHits, rows);
    }

    private List<FolderRowViewModel> BuildFolderRows(FolderComposition composition, long total, FileCategory? filter, DateTime? cutoff)
    {
        var rows = new List<FolderRowViewModel>();
        foreach (ChildComposition child in composition.Children)
        {
            long bytes = filter is FileCategory c ? child.SizeOf(c) : child.TotalSizeOnDisk;
            bool special = child.Node.State != DirectoryState.Scanned;

            // Nicht lesbare Ordner bleiben sichtbar (ohne Filter), auch ohne Größe.
            if (bytes <= 0 && !(special && filter is null))
            {
                continue;
            }

            // Ordner ohne bekannte Änderungszeit (nicht lesbar) lassen sich nicht als "alt" einstufen.
            if (cutoff is DateTime limit && (special || child.Node.NewestWriteUtc >= limit))
            {
                continue;
            }

            rows.Add(CreateFolderRow(child, bytes, total, filter, composition.Folder));
        }

        AddFileRows(rows, composition, total, filter, cutoff);
        return rows;
    }

    /// <summary>
    /// Suchtreffer als Zeilen. Dateien bekommen ihre Kategorie über die Pfadregeln; Ordner haben hier keine Aufteilung
    /// nach Typen (sie entsteht beim Öffnen), und fallen bei aktivem Typ-Filter weg.
    /// </summary>
    private List<FolderRowViewModel> BuildSearchRows(
        SearchResult result, DirectoryNode scope, long shareBase, FileCategory? filter, DateTime? cutoff)
    {
        var rows = new List<FolderRowViewModel>();
        string scopePath = scope.FullPath;
        foreach (SearchHit hit in result.Hits)
        {
            string location = Path.GetRelativePath(scopePath, hit.Folder.FullPath);
            if (hit.File is FileEntry file)
            {
                FileCategory category = CategoryAnalyzer.CategorizeFile(hit.Folder, file);
                if ((filter is not null && category != filter) || (cutoff is DateTime limit && file.LastWriteUtc >= limit))
                {
                    continue;
                }

                rows.Add(CreateFileRow(hit.Folder, file, category, shareBase, $"in {Describe(location)}"));
            }
            else
            {
                DirectoryNode node = hit.Folder;
                if (filter is not null || (cutoff is DateTime limit2 && (node.State != DirectoryState.Scanned || node.NewestWriteUtc >= limit2)))
                {
                    continue;
                }

                rows.Add(CreateSearchFolderRow(node, shareBase, $"in {Describe(Path.GetRelativePath(scopePath, node.Parent?.FullPath ?? scopePath))}"));
            }
        }

        return rows;

        static string Describe(string relative) => relative == "." ? "diesem Ordner" : relative;
    }

    /// <summary>Die Sammelzeile steht am Listenende: Sie ist größer als jede einzelne Datei, aber kein echter Eintrag.</summary>
    private FolderRowViewModel[] Order(FolderRowViewModel[] bySize)
    {
        IEnumerable<FolderRowViewModel> real = bySize.Where(r => r.Kind != RowKind.MoreFiles);
        real = Sort switch
        {
            PlacesSort.Name => real
                .OrderBy(r => r.Kind == RowKind.Folder ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            PlacesSort.Modified => real
                .OrderByDescending(r => r.ModifiedUtc)
                .ThenByDescending(r => r.Bytes),
            _ => real,
        };

        return real.Concat(bySize.Where(r => r.Kind == RowKind.MoreFiles)).ToArray();
    }

    private string BuildHeader(string where, long total, FileCategory? filter, bool stale, bool showHits, List<FolderRowViewModel> rows)
    {
        if (showHits)
        {
            SearchResult result = _searchResult!;
            string shown = result.TotalMatches > result.Hits.Count
                ? $" · die {result.Hits.Count:N0} größten gezeigt"
                : string.Empty;
            return string.Create(CultureInfo.CurrentCulture, $"{result.TotalMatches:N0} Treffer für „{_searchText.Trim()}“ in {where}{shown}");
        }

        string typeName = filter is FileCategory shownCategory ? " " + CategoryCatalog.Get(shownCategory).Name : string.Empty;
        return stale
            ? $"{ByteFormat.Format(rows.Sum(r => r.Bytes))}{typeName}, seit über einem Jahr unverändert, in {where}"
            : $"{ByteFormat.Format(total)}{typeName} in {where}";
    }

    private async Task RunSearchAsync()
    {
        // Die Quellen werden nur abgebrochen, nicht entsorgt: Eine laufende Suche liest ihr Token noch.
        // Ohne Zeitgeber oder verknüpfte Token hält eine Quelle keine Ressourcen, die freizugeben wären.
        _searchCts?.Cancel();
        _searchCts = null;

        string query = _searchText.Trim();
        DirectoryNode? scope = _current;
        if (query.Length < MinSearchLength || scope is null)
        {
            _searchResult = null;
            IsSearchBusy = false;
            RebuildRows(); // zurück zur Ordnerliste
            return;
        }

        var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        _searchCts = cts;
        IsSearchBusy = true;
        HeaderText = "Suche …";
        try
        {
            await Task.Delay(250, token); // entprellen, solange noch getippt wird
            SearchResult result = await Task.Run(() => TreeSearch.Find(scope, query, MaxSearchRows, token), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _searchResult = result;
            RebuildRows();
        }
        catch (OperationCanceledException)
        {
            // Eine neuere Eingabe hat die Suche abgelöst.
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearchBusy = false;
            }
        }
    }

    /// <summary>Beendet eine laufende Suche und leert das Suchfeld, ohne die Liste neu aufzubauen.</summary>
    private void ResetSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchResult = null;
        IsSearchBusy = false;
        if (_searchText.Length > 0)
        {
            _searchText = string.Empty;
            Raise(nameof(SearchText));
            Raise(nameof(IsSearching));
        }
    }

    private void ShowInFolder()
    {
        FolderRowViewModel? row = SelectedRow;
        if (row?.ContainingFolder is DirectoryNode folder)
        {
            _pendingSelectKey = row.Key;
            _ = ShowFolderAsync(folder);
        }
    }

    private void Export()
    {
        string[] header = { "Typ", "Name", "Pfad", "Belegung (Byte)", "Dateigröße (Byte)", "Anteil (%)", "Kategorie", "Dateien", "Zuletzt geändert" };
        IEnumerable<IReadOnlyList<string>> lines = Rows.Select(r => (IReadOnlyList<string>)new[]
        {
            r.TypeText,
            r.Name,
            r.FullPath,
            r.Bytes.ToString(CultureInfo.InvariantCulture),
            r.LogicalBytes.ToString(CultureInfo.InvariantCulture),
            r.ShareValue.ToString("0.0", CultureInfo.CurrentCulture),
            r.CategoryName,
            r.FileCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            r.ModifiedUtc == DateTime.MinValue ? string.Empty : r.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        });

        string? message = ExportService.SaveCsv("Liste exportieren", ExportService.SuggestName("Orte", ".csv"), header, lines);
        if (message is not null)
        {
            Notify?.Invoke(message);
        }
    }

    private static FolderRowViewModel CreateFolderRow(ChildComposition child, long bytes, long total, FileCategory? filter, DirectoryNode containing)
    {
        DirectoryNode node = child.Node;

        // Mit Typ-Filter gibt es weder gefilterte Dateizahl noch gefilterte Spur; dann nur der Anteil.
        string? detail = filter is null ? WithModified(FolderCounts(node), node.NewestWriteUtc, "Inhalt zuletzt geändert") : null;
        string? trail = filter is null && child.Trail is HeavyTrail t
            ? $"↳ {string.Join(" › ", t.Names)} · {ByteFormat.Format(t.SizeOnDisk)}"
            : null;

        return new FolderRowViewModel(
            RowKind.Folder, node, node.Name, node.FullPath, bytes, total,
            BrushFor(filter ?? child.Dominant), child.SizeByCategory, detail, FolderInfo(node), trail)
        {
            LogicalBytes = node.TotalLogicalSize,
            FileCount = node.TotalFileCount,
            CategoryName = (filter ?? child.Dominant) is FileCategory dominant ? CategoryCatalog.Get(dominant).Name : string.Empty,
            ModifiedUtc = node.NewestWriteUtc,
            ContainingFolder = containing,
        };
    }

    /// <summary>Ordner als Suchtreffer: ohne Aufteilung nach Typen (die entsteht erst beim Öffnen) und ohne Spur.</summary>
    private static FolderRowViewModel CreateSearchFolderRow(DirectoryNode node, long total, string location)
        => new(
            RowKind.Folder, node, node.Name, node.FullPath, node.TotalSizeOnDisk, total,
            CategoryCatalog.UnattributedBrush, new long[CategoryCount],
            WithModified(FolderCounts(node), node.NewestWriteUtc, "Inhalt zuletzt geändert"), FolderInfo(node), location)
        {
            LogicalBytes = node.TotalLogicalSize,
            FileCount = node.TotalFileCount,
            ModifiedUtc = node.NewestWriteUtc,
            ContainingFolder = node.Parent,
        };

    private static string FolderInfo(DirectoryNode node)
    {
        string info = $"{ByteFormat.Format(node.TotalSizeOnDisk)} belegt · {ByteFormat.Format(node.TotalLogicalSize)} Dateigröße · {FolderCounts(node)}";
        if (node.NewestWriteUtc != DateTime.MinValue)
        {
            info += $" · Inhalt zuletzt geändert {node.NewestWriteUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}";
        }

        return node.State switch
        {
            DirectoryState.Unreadable => $"{info} · nicht lesbar: {node.ErrorMessage}",
            DirectoryState.LinkSkipped => $"Verknüpfung{(node.LinkTarget is null ? string.Empty : $" auf {node.LinkTarget}")}, nicht durchsucht",
            _ => info,
        };
    }

    private static string WithModified(string text, DateTime utc, string label)
        => utc == DateTime.MinValue ? text : $"{text} · {label} {utc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}";

    private static FolderRowViewModel CreateFileRow(DirectoryNode folder, FileEntry file, FileCategory category, long total, string? location)
    {
        CategoryInfo info = CategoryCatalog.Get(category);
        string extension = Path.GetExtension(file.Name);
        var sizes = new long[CategoryCount];
        sizes[(int)category] = file.SizeOnDisk;

        string kind = extension.Length > 0 ? $"{info.Name} · {extension}" : info.Name;
        string date = file.LastWriteUtc == DateTime.MinValue ? string.Empty : file.LastWriteUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        string detail = date.Length == 0 ? kind : $"{kind} · geändert {date}";
        string text = $"{ByteFormat.Format(file.SizeOnDisk)} belegt · {ByteFormat.Format(file.Length)} Dateigröße"
            + (date.Length == 0 ? string.Empty : $" · zuletzt geändert {date}");

        return new FolderRowViewModel(
            RowKind.File, null, file.Name, Path.Combine(folder.FullPath, file.Name), file.SizeOnDisk, total,
            info.Brush, sizes, detail, text, location)
        {
            LogicalBytes = file.Length,
            CategoryName = info.Name,
            ModifiedUtc = file.LastWriteUtc,
            ContainingFolder = folder,
        };
    }

    /// <summary>
    /// Die größten Dateien direkt im Ordner als eigene Zeilen, der Rest als Sammelzeile. Dateien ohne Belegung
    /// (leer, Cloud-Platzhalter) erscheinen nicht, wie bei den Ordnern.
    /// </summary>
    private static void AddFileRows(List<FolderRowViewModel> rows, FolderComposition composition, long total, FileCategory? filter, DateTime? cutoff)
    {
        string folderPath = composition.Folder.FullPath;
        OwnFile[] sized = composition.OwnFiles
            .Where(f => f.File.SizeOnDisk > 0
                && (filter is null || f.Category == filter)
                && (cutoff is null || f.File.LastWriteUtc < cutoff))
            .OrderByDescending(f => f.File.SizeOnDisk)
            .ToArray();

        foreach (OwnFile own in sized.Take(MaxFileRows))
        {
            rows.Add(CreateFileRow(composition.Folder, own.File, own.Category, total, null));
        }

        OwnFile[] rest = sized.Skip(MaxFileRows).ToArray();
        if (rest.Length == 0)
        {
            return;
        }

        var restSizes = new long[CategoryCount];
        foreach (OwnFile own in rest)
        {
            restSizes[(int)own.Category] += own.File.SizeOnDisk;
        }

        long restBytes = restSizes.Sum();
        string count = string.Create(CultureInfo.CurrentCulture, $"{rest.Length:N0} weitere Dateien");
        rows.Add(new FolderRowViewModel(
            RowKind.MoreFiles, null, count, folderPath, restBytes, total,
            BrushFor(filter ?? CompositionMath.Dominant(restSizes)), restSizes, null,
            $"{ByteFormat.Format(restBytes)} belegt · jede höchstens {ByteFormat.Format(sized[MaxFileRows - 1].File.SizeOnDisk)}")
        {
            LogicalBytes = rest.Sum(r => r.File.Length),
            FileCount = rest.Length,
            ContainingFolder = composition.Folder,
        });
    }

    private static string FolderCounts(DirectoryNode node)
    {
        string files = string.Create(
            CultureInfo.CurrentCulture, $"{node.TotalFileCount:N0} {(node.TotalFileCount == 1 ? "Datei" : "Dateien")}");
        return node.TotalSubdirectoryCount > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{files} · {node.TotalSubdirectoryCount:N0} Ordner")
            : files;
    }

    private static IReadOnlyList<TreemapItem> BuildTreemapItems(FolderRowViewModel[] sorted)
    {
        var items = new List<TreemapItem>();
        foreach (FolderRowViewModel row in sorted.Where(r => r.Bytes > 0).Take(MaxTreemapItems))
        {
            items.Add(new TreemapItem(row, row.Name, row.SizeText, row.Bytes, row.Brush));
        }

        FolderRowViewModel[] rest = sorted.Where(r => r.Bytes > 0).Skip(MaxTreemapItems).ToArray();
        if (rest.Length > 0)
        {
            long bytes = rest.Sum(r => r.Bytes);
            items.Add(new TreemapItem(
                null,
                string.Create(CultureInfo.CurrentCulture, $"{rest.Length:N0} weitere"),
                ByteFormat.Format(bytes),
                bytes,
                BucketBrush));
        }

        return items;
    }

    private void RebuildComposition()
    {
        FolderRowViewModel? row = SelectedRow;
        if (row is null)
        {
            CompositionTitle = string.Empty;
            CompositionTotal = 0;
            CompositionSegments = Array.Empty<BarSegment>();
            CompositionItems = Array.Empty<CompositionItemViewModel>();
            return;
        }

        IReadOnlyList<long> sizes = row.SizeByCategory;
        long total = sizes.Sum();
        var segments = new List<BarSegment>();
        var entries = new List<(FileCategory Category, long Bytes)>();
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            long bytes = sizes[(int)category];
            if (bytes > 0)
            {
                segments.Add(new BarSegment(category, bytes, CategoryCatalog.Get(category).Brush));
                entries.Add((category, bytes));
            }
        }

        CompositionTitle = $"Zusammensetzung · {row.Name}";
        CompositionTotal = total;
        CompositionSegments = segments;
        CompositionItems = entries
            .OrderByDescending(e => e.Bytes)
            .Take(MaxCompositionItems)
            .Select(e => new CompositionItemViewModel(
                CategoryCatalog.Get(e.Category).Name,
                CategoryCatalog.Get(e.Category).Brush,
                ByteFormat.Format(e.Bytes),
                ((double)e.Bytes / total).ToString("P0", CultureInfo.CurrentCulture)))
            .ToArray();
    }

    private static Brush BrushFor(FileCategory? category)
        => category is FileCategory c ? CategoryCatalog.Get(c).Brush : CategoryCatalog.UnattributedBrush;

    private static Brush CreateBucketBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x40));
        brush.Freeze();
        return brush;
    }
}
