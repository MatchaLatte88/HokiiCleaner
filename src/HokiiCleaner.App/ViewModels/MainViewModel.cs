using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using HokiiCleaner.Actions;
using HokiiCleaner.App.Controls;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.App.Presentation;
using HokiiCleaner.Core;
using HokiiCleaner.Core.Categorization;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.App.ViewModels;

public enum ScanState
{
    Idle,
    Scanning,
    Ready,
    Failed,
}

public enum ViewMode
{
    Sonar,
    Types,
    Places,
    Programs,
    Duplicates,
    Dev,
    Bin,
    History,
}

/// <summary>Ein Eintrag im Platzgewinn-Potenzial: eine Kategorie, deren Inhalt Windows oder Programme bei Bedarf neu anlegen.</summary>
public sealed record PotentialItemViewModel(FileCategory Category, string Name, System.Windows.Media.Brush Brush, string SizeText, long Bytes);

/// <summary>Oberste Navigation: Speicher-Analyse, Langsam-Check oder Aufräumen (der einzige Bereich, der löscht).</summary>
public enum Section
{
    Storage,
    Slowness,
    Clean,
}

public sealed class MainViewModel : ObservableObject
{
    private Section _section = Section.Storage;
    private ViewMode _viewMode = ViewMode.Sonar;
    private CancellationTokenSource? _cts;
    private CategoryBreakdown? _breakdown;
    private ScanState _state = ScanState.Idle;
    private DriveViewModel? _selectedDrive;
    private string _heroValue = string.Empty;
    private string _heroCaption = string.Empty;
    private string _statusText = string.Empty;
    private string _errorText = string.Empty;
    private string? _unattributedText;
    private string? _issueText;
    private double _scanFraction = -1;
    private long _barTotalBytes;
    private IReadOnlyList<BarSegment> _barSegments = Array.Empty<BarSegment>();
    private IReadOnlyList<CategoryRowViewModel> _rows = Array.Empty<CategoryRowViewModel>();
    private IReadOnlyList<FileRowViewModel> _topFiles = Array.Empty<FileRowViewModel>();
    private FileCategory? _selectedCategory;
    private FileCategory? _hoveredCategory;
    private IReadOnlyList<PotentialItemViewModel> _potentialItems = Array.Empty<PotentialItemViewModel>();
    private string _potentialTotalText = string.Empty;
    private ScanResult? _scan;
    private string _scanTarget = string.Empty;
    private DriveViewModel? _scanDrive;

    public MainViewModel()
    {
        Drives = DriveViewModel.LoadFixedDrives();
        _selectedDrive = Drives.FirstOrDefault(d => d.Label.Equals("C:", StringComparison.OrdinalIgnoreCase))
            ?? Drives.FirstOrDefault();
        if (_selectedDrive is null)
        {
            _errorText = "Es wurde kein Festplattenlaufwerk gefunden.";
            _state = ScanState.Failed;
        }

        StartCommand = new RelayCommand(() => _ = StartScanAsync(), () => CanStart);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsScanning);
        Settings = UserSettings.Load(out string? settingsProblem);
        if (settingsProblem is not null)
        {
            _statusText = settingsProblem;
        }

        ProtectedFolders = Settings.ProtectedFolders.ToArray();
        Basket = new BasketViewModel(
            OnItemsMovedAsync,
            () => Duplicates!.GroupPaths(), // wird erst nach der Konstruktion aufgerufen
            () => ViewMode = ViewMode.Bin,
            PathGuard.ForCurrentUser(Settings.ProtectedFolders))
        {
            Notify = message => StatusText = message,
        };
        ProtectFolderCommand = new RelayCommand(ProtectSelectedFolder, () => Places!.SelectedRow is { Kind: RowKind.Folder }); // Places entsteht gleich darunter
        UnprotectFolderCommand = new RelayCommand<string>(UnprotectFolder);
        Places = new PlacesViewModel(Basket) { Notify = message => StatusText = message };
        Sonar = new SonarViewModel(Places);
        Programs = new ProgramsViewModel(ShowInPlaces) { Notify = message => StatusText = message };
        Duplicates = new DuplicatesViewModel(() => Places.CurrentFolder, ShowInPlaces, Basket) { Notify = message => StatusText = message };
        Dev = new DevViewModel(Basket);
        Bin = new BinViewModel(OnBinChangedAsync) { Notify = message => StatusText = message };
        History = new HistoryViewModel(() => _scan?.Root) { Notify = message => StatusText = message };
        Basket.Changed += (_, _) => RefreshTopFileMarks();
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsElevated);
        ShowWhereCommand = new RelayCommand(ShowWhere, () => SelectedCategory is not null);
        ShowPotentialCommand = new RelayCommand<PotentialItemViewModel>(item =>
        {
            SelectedCategory = item.Category;
            ViewMode = ViewMode.Types;
        });
        ExportTopFilesCommand = new RelayCommand(ExportTopFiles, () => TopFiles.Count > 0);
    }

    public IReadOnlyList<DriveViewModel> Drives { get; }

    public PlacesViewModel Places { get; }

    public SonarViewModel Sonar { get; }

    public ProgramsViewModel Programs { get; }

    public DuplicatesViewModel Duplicates { get; }

    /// <summary>Der Auswahlkorb: Dateien und Ordner, die der Benutzer in den Papierkorb legen möchte.</summary>
    public BasketViewModel Basket { get; }

    public DevViewModel Dev { get; }

    public BinViewModel Bin { get; }

    public HistoryViewModel History { get; }

    /// <summary>Läuft HokiiCleaner mit Administratorrechten? Ohne sie bleiben manche Ordner unlesbar und manche Ziele unlöschbar.</summary>
    public bool IsElevated { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public bool NotElevated => !IsElevated;

    private UserSettings Settings { get; }

    private IReadOnlyList<string> _protectedFolders = Array.Empty<string>();

    /// <summary>Vom Benutzer geschützte Ordner (samt Inhalt); sie lassen sich nie für den Papierkorb vormerken.</summary>
    public IReadOnlyList<string> ProtectedFolders
    {
        get => _protectedFolders;
        private set
        {
            if (Set(ref _protectedFolders, value))
            {
                Raise(nameof(HasProtectedFolders));
            }
        }
    }

    public bool HasProtectedFolders => ProtectedFolders.Count > 0;

    /// <summary>Schützt den in Orte gewählten Ordner samt Inhalt vor dem Vormerken.</summary>
    public ICommand ProtectFolderCommand { get; }

    public ICommand UnprotectFolderCommand { get; }

    private void ProtectSelectedFolder()
    {
        if (Places.SelectedRow is not { Kind: RowKind.Folder } row || Settings.ProtectedFolders.Contains(row.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        ChangeProtection(() => Settings.ProtectedFolders.Add(row.FullPath), $"„{row.Name}“ ist jetzt geschützt und lässt sich nicht mehr vormerken.");
    }

    private void UnprotectFolder(string folder)
        => ChangeProtection(() => Settings.ProtectedFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)), "Der Schutz wurde aufgehoben.");

    private void ChangeProtection(Action change, string message)
    {
        change();
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht stillschweigend weitermachen: Die Änderung bleibt nur für diese Sitzung.
            StatusText = $"Die Einstellung ließ sich nicht speichern und gilt nur bis zum Beenden: {ex.Message}";
            ApplyProtection();
            return;
        }

        ApplyProtection();
        StatusText = message;
    }

    private void ApplyProtection()
    {
        ProtectedFolders = Settings.ProtectedFolders.ToArray();
        Basket.UseGuard(PathGuard.ForCurrentUser(Settings.ProtectedFolders));
        Places.Refresh();
        if (_scan is not null)
        {
            _ = Dev.LoadAsync(_scan.Root);
        }
    }

    /// <summary>Startet HokiiCleaner neu mit Administratorrechten (Windows fragt nach). Ein laufender Scan geht dabei verloren.</summary>
    public ICommand RestartAsAdminCommand { get; }

    private void RestartAsAdmin()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            StatusText = "Der Programmpfad ist nicht ermittelbar; Neustart als Administrator nicht möglich.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText = "Abgebrochen: Die Rückfrage von Windows wurde nicht bestätigt.";
        }
        catch (Win32Exception ex)
        {
            StatusText = $"Neustart als Administrator fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>Wählt eine Kategorie des Potenzials und zeigt sie in der Typen-Ansicht.</summary>
    public ICommand ShowPotentialCommand { get; }

    /// <summary>Speichert die größten Dateien der gewählten Kategorie als CSV.</summary>
    public ICommand ExportTopFilesCommand { get; }

    /// <summary>Wechselt in die Orte-Ansicht und öffnet den Ordner (z. B. aus der Programmliste oder den Duplikaten).</summary>
    private void ShowInPlaces(DirectoryNode folder)
    {
        ViewMode = ViewMode.Places;
        Places.OpenFolder(folder);
    }

    public SlownessViewModel Slow { get; } = new();

    public CleanViewModel Clean { get; } = new();

    public Section Section
    {
        get => _section;
        set
        {
            if (Set(ref _section, value))
            {
                Raise(nameof(ShowStorage));
                Raise(nameof(ShowSlowness));
                Raise(nameof(ShowClean));
                Raise(nameof(AccessText));
                if (value == Section.Clean)
                {
                    Clean.EnsureAnalyzed();
                }
            }
        }
    }

    /// <summary>Statuszeile rechts: was der Bereich verändern darf.</summary>
    public string AccessText => Section switch
    {
        Section.Clean => "Löscht nur nach Bestätigung",
        Section.Storage => "Liest; verschiebt nur nach Bestätigung in den Papierkorb",
        _ => "Nur lesender Zugriff",
    };

    public bool ShowClean
    {
        get => Section == Section.Clean;
        set
        {
            if (value)
            {
                Section = Section.Clean;
            }
        }
    }

    public bool ShowStorage
    {
        get => Section == Section.Storage;
        set
        {
            if (value)
            {
                Section = Section.Storage;
            }
        }
    }

    public bool ShowSlowness
    {
        get => Section == Section.Slowness;
        set
        {
            if (value)
            {
                Section = Section.Slowness;
            }
        }
    }

    /// <summary>Wechselt zur Orte-Ansicht, gefiltert auf den gewählten Typ ("Wo liegt das?").</summary>
    public ICommand ShowWhereCommand { get; }

    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (Set(ref _viewMode, value))
            {
                Raise(nameof(ShowSonar));
                Raise(nameof(ShowTypes));
                Raise(nameof(ShowPlaces));
                Raise(nameof(ShowPrograms));
                Raise(nameof(ShowDuplicates));
                Raise(nameof(ShowDev));
                Raise(nameof(ShowBin));
                Raise(nameof(ShowHistory));
                RaiseViewVisibility();
                Raise(nameof(ShowEmpty));
                Raise(nameof(ShowError));
                if (value == ViewMode.Bin)
                {
                    Bin.EnsureLoaded();
                }

                if (value == ViewMode.History)
                {
                    History.EnsureLoaded();
                }
            }
        }
    }

    private void RaiseViewVisibility()
    {
        Raise(nameof(ShowSonarView));
        Raise(nameof(ShowListsView));
        Raise(nameof(ShowProgramsView));
        Raise(nameof(ShowDuplicatesView));
        Raise(nameof(ShowDevView));
        Raise(nameof(ShowBinView));
        Raise(nameof(ShowHistoryView));
    }

    public bool ShowDev
    {
        get => ViewMode == ViewMode.Dev;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.Dev;
            }
        }
    }

    public bool ShowBin
    {
        get => ViewMode == ViewMode.Bin;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.Bin;
            }
        }
    }

    public bool ShowHistory
    {
        get => ViewMode == ViewMode.History;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.History;
            }
        }
    }

    /// <summary>Entwickler-Funde brauchen das fertige Scan-Ergebnis.</summary>
    public bool ShowDevView => HasResult && ViewMode == ViewMode.Dev;

    /// <summary>Papierkorb und Verlauf brauchen keinen Scan; sie ersetzen die Startansicht.</summary>
    public bool ShowBinView => ViewMode == ViewMode.Bin;

    public bool ShowHistoryView => ViewMode == ViewMode.History;

    private bool IsStandaloneView => ViewMode is ViewMode.Bin or ViewMode.History;

    public bool ShowPrograms
    {
        get => ViewMode == ViewMode.Programs;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.Programs;
            }
        }
    }

    public bool ShowDuplicates
    {
        get => ViewMode == ViewMode.Duplicates;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.Duplicates;
            }
        }
    }

    /// <summary>Die Programmliste braucht das fertige Scan-Ergebnis; sie hat keine eigene Kennzahl.</summary>
    public bool ShowProgramsView => HasResult && ViewMode == ViewMode.Programs;

    public bool ShowDuplicatesView => HasResult && ViewMode == ViewMode.Duplicates;

    public bool ShowSonar
    {
        get => ViewMode == ViewMode.Sonar;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.Sonar;
            }
        }
    }

    /// <summary>Der Sonar-Ring ersetzt in dieser Ansicht Kennzahl und Messband.</summary>
    public bool ShowSonarView => ShowHero && ViewMode == ViewMode.Sonar;


    /// <summary>Kennzahl, Messband und die Listen (Typen oder Orte); während des Scans auch in den Ansichten Programme und Duplikate.</summary>
    public bool ShowListsView => ShowHero && ViewMode != ViewMode.Sonar && !ShowProgramsView && !ShowDuplicatesView && !ShowDevView
        && !IsStandaloneView;

    public bool ThemeSystem
    {
        get => ThemeService.Mode == ThemeMode.System;
        set => SetTheme(value, ThemeMode.System);
    }

    public bool ThemeLight
    {
        get => ThemeService.Mode == ThemeMode.Light;
        set => SetTheme(value, ThemeMode.Light);
    }

    public bool ThemeDark
    {
        get => ThemeService.Mode == ThemeMode.Dark;
        set => SetTheme(value, ThemeMode.Dark);
    }

    private void SetTheme(bool selected, ThemeMode mode)
    {
        if (!selected || ThemeService.Mode == mode)
        {
            return;
        }

        ThemeService.SetMode(mode);
        Raise(nameof(ThemeSystem));
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
        if (ThemeService.Warning is not null)
        {
            StatusText = ThemeService.Warning;
        }
    }

    public bool ShowTypes
    {
        get => ViewMode == ViewMode.Types;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.Types;
            }
        }
    }

    public bool ShowPlaces
    {
        get => ViewMode == ViewMode.Places;
        set
        {
            if (value)
            {
                ViewMode = ViewMode.Places;
            }
        }
    }

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public DriveViewModel? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (Set(ref _selectedDrive, value))
            {
                Raise(nameof(CanStart));
                Raise(nameof(IdleTotalBytes));
                Raise(nameof(IdleUsedBytes));
                Raise(nameof(IdleBig));
                Raise(nameof(IdleLabel));
                Raise(nameof(IdleSub));
            }
        }
    }

    /// <summary>Werte des Laufwerksrings vor dem ersten Scan; sie stammen aus der Laufwerksbelegung, nicht aus einer Analyse.</summary>
    public long IdleTotalBytes => SelectedDrive?.TotalBytes ?? 0;

    public long IdleUsedBytes => SelectedDrive?.UsedBytes ?? 0;

    public string IdleBig => SelectedDrive is null ? string.Empty : ByteFormat.Split(SelectedDrive.UsedBytes).Number;

    public string IdleLabel => SelectedDrive is null
        ? string.Empty
        : $"{ByteFormat.Split(SelectedDrive.UsedBytes).Unit} belegt von {ByteFormat.Format(SelectedDrive.TotalBytes)}";

    public string IdleSub => SelectedDrive is null || SelectedDrive.TotalBytes <= 0
        ? string.Empty
        : ((double)SelectedDrive.FreeBytes / SelectedDrive.TotalBytes).ToString("P0", CultureInfo.CurrentCulture) + " FREI";

    public ScanState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value))
            {
                return;
            }

            Raise(nameof(IsScanning));
            Raise(nameof(HasResult));
            Raise(nameof(ShowEmpty));
            Raise(nameof(ShowError));
            Raise(nameof(ShowHero));
            RaiseViewVisibility();
            Raise(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsScanning => State == ScanState.Scanning;

    public bool HasResult => State == ScanState.Ready;

    public bool ShowEmpty => State == ScanState.Idle && !IsStandaloneView;

    public bool ShowError => State == ScanState.Failed && !IsStandaloneView;

    public bool ShowHero => IsScanning || HasResult;

    public bool CanStart => !IsScanning && SelectedDrive is not null;

    public string HeroValue
    {
        get => _heroValue;
        private set => Set(ref _heroValue, value);
    }

    public string HeroCaption
    {
        get => _heroCaption;
        private set => Set(ref _heroCaption, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    /// <summary>Hinweis auf Platz, der nicht einer Kategorie zugeordnet werden konnte; <c>null</c> = nichts anzuzeigen.</summary>
    public string? UnattributedText
    {
        get => _unattributedText;
        private set
        {
            if (Set(ref _unattributedText, value))
            {
                Raise(nameof(HasUnattributedText));
            }
        }
    }

    public bool HasUnattributedText => !string.IsNullOrEmpty(UnattributedText);

    /// <summary>
    /// Platzgewinn-Potenzial: Papierkorb, Temp und Caches und die alte Windows-Installation. Das ist eine Orientierung,
    /// was Windows und Programme bei Bedarf selbst neu anlegen oder entfernen; diese Anzeige löscht nichts (das geschieht nur im Bereich Aufräumen).
    /// </summary>
    public IReadOnlyList<PotentialItemViewModel> PotentialItems
    {
        get => _potentialItems;
        private set
        {
            if (Set(ref _potentialItems, value))
            {
                Raise(nameof(HasPotential));
            }
        }
    }

    public bool HasPotential => PotentialItems.Count > 0;

    public string PotentialTotalText
    {
        get => _potentialTotalText;
        private set => Set(ref _potentialTotalText, value);
    }

    /// <summary>Was passiert, wenn der Inhalt der gewählten Kategorie wegfällt; leer ohne Auswahl.</summary>
    public string SelectedHint => SelectedCategory is FileCategory category ? CategoryCatalog.Get(category).Hint : string.Empty;

    /// <summary>Warnung bei unvollständigem Scan; <c>null</c> = nichts anzuzeigen.</summary>
    public string? IssueText
    {
        get => _issueText;
        private set
        {
            if (Set(ref _issueText, value))
            {
                Raise(nameof(HasIssueText));
            }
        }
    }

    public bool HasIssueText => !string.IsNullOrEmpty(IssueText);

    public double ScanFraction
    {
        get => _scanFraction;
        private set
        {
            if (Set(ref _scanFraction, value))
            {
                Sonar.ScanFraction = value;
            }
        }
    }

    public long BarTotalBytes
    {
        get => _barTotalBytes;
        private set => Set(ref _barTotalBytes, value);
    }

    public IReadOnlyList<BarSegment> BarSegments
    {
        get => _barSegments;
        private set => Set(ref _barSegments, value);
    }

    public IReadOnlyList<CategoryRowViewModel> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public IReadOnlyList<FileRowViewModel> TopFiles
    {
        get => _topFiles;
        private set => Set(ref _topFiles, value);
    }

    public string TopFilesTitle => SelectedRow is null ? "Größte Dateien" : $"Größte Dateien · {SelectedRow.Name}";

    public FileCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value))
            {
                return;
            }

            Raise(nameof(SelectedRow));
            Raise(nameof(TopFilesTitle));
            Raise(nameof(SelectedHint));
            UpdateTopFiles();
            Places.FocusCategory = value;
        }
    }

    public CategoryRowViewModel? SelectedRow
    {
        get => Rows.FirstOrDefault(r => r.Category == _selectedCategory);
        set => SelectedCategory = value?.Category;
    }

    public FileCategory? HoveredCategory
    {
        get => _hoveredCategory;
        set
        {
            if (!Set(ref _hoveredCategory, value))
            {
                return;
            }

            foreach (CategoryRowViewModel row in Rows)
            {
                row.IsHovered = row.Category == value;
            }
        }
    }

    /// <summary>Startet die Analyse des gewählten Laufwerks oder, wenn angegeben, eines beliebigen Ordners.</summary>
    public async Task StartScanAsync(string? path = null)
    {
        if (IsScanning)
        {
            return;
        }

        string target;
        DriveViewModel? drive;
        if (path is null)
        {
            if (SelectedDrive is null)
            {
                ErrorText = "Kein Laufwerk ausgewählt.";
                State = ScanState.Failed;
                return;
            }

            target = SelectedDrive.RootPath;
            drive = SelectedDrive;
        }
        else
        {
            target = Path.GetFullPath(path);
            drive = Drives.FirstOrDefault(d => string.Equals(
                d.RootPath.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }

        _cts = new CancellationTokenSource();
        CancellationToken cancellationToken = _cts.Token;

        ClearResult();
        ErrorText = string.Empty;
        StatusText = target;
        HeroValue = "Analysiere …";
        HeroCaption = string.Empty;
        ScanFraction = drive is null ? -1 : 0;
        State = ScanState.Scanning;

        var progress = new Progress<ScanProgress>(p => OnProgress(p, drive));
        try
        {
            ScanResult scan = await DiskScanner.ScanAsync(target, progress: progress, cancellationToken: cancellationToken);
            CategoryBreakdown breakdown = await Task.Run(() => CategoryAnalyzer.Analyze(scan.Root), cancellationToken);

            _breakdown = breakdown;
            _scan = scan;
            _scanTarget = target;
            _scanDrive = drive;
            ApplyResult(target, drive, scan, breakdown, refresh: false);
            State = ScanState.Ready;
        }
        catch (OperationCanceledException)
        {
            ClearResult();
            StatusText = "Analyse abgebrochen.";
            State = ScanState.Idle;
        }
        catch (Exception ex)
        {
            ClearResult();
            ErrorText = ex.Message;
            State = ScanState.Failed;
        }
        finally
        {
            ScanFraction = -1;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Zeigt die Datei im Explorer. Verändert nichts.</summary>
    public void Reveal(FileRowViewModel file)
    {
        try
        {
            ExplorerLauncher.Reveal(file.FullPath);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            StatusText = $"Explorer konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    private void OnProgress(ScanProgress p, DriveViewModel? drive)
    {
        if (State != ScanState.Scanning)
        {
            return; // Nachzügler nach Ende oder Abbruch nicht mehr anzeigen
        }

        HeroCaption = string.Create(
            CultureInfo.CurrentCulture,
            $"{p.Files:N0} Dateien · {p.Directories:N0} Ordner · {p.Elapsed:m\\:ss}");
        StatusText = p.CurrentDirectory;
        if (drive is not null && drive.UsedBytes > 0)
        {
            ScanFraction = Math.Min(0.99, (double)p.LogicalBytes / drive.UsedBytes);
        }
    }

    private void ApplyResult(string target, DriveViewModel? drive, ScanResult scan, CategoryBreakdown breakdown, bool refresh)
    {
        long scanned = breakdown.TotalSizeOnDisk;
        long total = scanned;
        long used = scanned;
        long unattributed = 0;
        string caption;

        if (drive is not null)
        {
            // Frisch lesen: Der Füllstand kann sich seit dem Start des Programms geändert haben.
            var info = new DriveInfo(drive.RootPath);
            total = info.TotalSize;
            long free = info.AvailableFreeSpace;
            used = total - free;
            unattributed = Math.Max(0, used - scanned);
            double freeShare = total == 0 ? 0 : (double)free / total;
            caption = string.Create(
                CultureInfo.CurrentCulture,
                $"von {ByteFormat.Format(total)} · {ByteFormat.Format(free)} frei ({freeShare:P0})");
        }
        else
        {
            caption = string.Create(CultureInfo.CurrentCulture, $"in {target} · {scan.Root.TotalFileCount:N0} Dateien");
        }

        var rows = Enum.GetValues<FileCategory>()
            .Where(c => breakdown[c].SizeOnDisk > 0)
            .Select(c => new CategoryRowViewModel(CategoryCatalog.Get(c), breakdown[c], scanned))
            .OrderByDescending(r => r.SizeOnDisk)
            .ToArray();

        var segments = new List<BarSegment>();
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            long bytes = breakdown[category].SizeOnDisk;
            if (bytes > 0)
            {
                segments.Add(new BarSegment(category, bytes, CategoryCatalog.Get(category).Brush));
            }
        }

        if (unattributed > 0)
        {
            segments.Add(new BarSegment(null, unattributed, CategoryCatalog.UnattributedBrush, Hatched: true));
        }

        BarTotalBytes = drive is not null ? total : scanned;
        BarSegments = segments;
        Rows = rows;
        HeroValue = ByteFormat.Format(used);
        HeroCaption = caption;
        UnattributedText = unattributed > 0
            ? $"{ByteFormat.Format(unattributed)} nicht zugeordnet, z. B. gesperrte Ordner oder Dateisystem-Metadaten"
            : null;
        IssueText = scan.Issues.Count > 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{scan.Issues.Count:N0} Ordner oder Dateien nicht lesbar, meist fehlende Zugriffsrechte. Das Ergebnis ist unvollständig.")
            : null;
        if (!refresh)
        {
            StatusText = string.Create(
                CultureInfo.CurrentCulture,
                $"{scan.Root.TotalFileCount:N0} Dateien in {scan.Elapsed.TotalSeconds:N1} s analysiert");
        }

        FileCategory? keep = refresh && rows.Any(r => r.Category == _selectedCategory) ? _selectedCategory : rows.FirstOrDefault()?.Category;
        SelectedCategory = keep;
        UpdateTopFiles(); // auch wenn die Auswahl gleich blieb, der Inhalt hat sich geändert
        UpdatePotential(breakdown);
        if (refresh)
        {
            return;
        }

        Places.Load(scan.Root, drive is not null ? new DriveContext(total, used) : null);
        _ = Programs.LoadAsync(scan.Root);
        _ = Dev.LoadAsync(scan.Root);
        History.OnScanChanged();
    }

    /// <summary>Nach dem Verschieben in den Papierkorb: Scan-Baum, Papierkorb-Zweig und alle Ansichten nachführen.</summary>
    private async Task OnItemsMovedAsync(IReadOnlyList<RecycleCandidate> moved)
    {
        ScanResult? scan = _scan;
        if (scan is null)
        {
            return;
        }

        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecycleCandidate item in moved)
        {
            ScanTreeEditor.Remove(scan.Root, item.Path);
            if (Path.GetPathRoot(item.Path) is { Length: > 0 } root)
            {
                drives.Add(root);
            }
        }

        await ResyncBinFolderAsync(scan, drives);
        Duplicates.RemoveFiles(moved.Select(m => m.Path).ToArray());
        Bin.MarkStale();
        await RefreshAfterChangesAsync(scan);
        StatusText = string.Create(
            CultureInfo.CurrentCulture,
            $"{moved.Count:N0} {(moved.Count == 1 ? "Eintrag" : "Einträge")} im Papierkorb. Der Platz wird frei, sobald der Papierkorb geleert ist.");
    }

    /// <summary>Der Papierkorb wurde geleert oder etwas wiederhergestellt: den Papierkorb-Zweig des Scans neu lesen.</summary>
    private async Task OnBinChangedAsync()
    {
        ScanResult? scan = _scan;
        if (scan is null)
        {
            return;
        }

        var drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            drives.Add(drive.RootDirectory.FullName);
        }

        await ResyncBinFolderAsync(scan, drives);
        await RefreshAfterChangesAsync(scan);
    }

    /// <summary>
    /// Liest den Ordner des Benutzers im Papierkorb jedes betroffenen Laufwerks neu und ersetzt ihn im Scan-Baum. Das ist genauer
    /// als Namen zu erfinden, denn der Papierkorb benennt seine Einträge selbst um.
    /// </summary>
    private async Task ResyncBinFolderAsync(ScanResult scan, IEnumerable<string> driveRoots)
    {
        foreach (string drive in driveRoots)
        {
            string folder = RecycleBin.UserFolder(drive);
            DirectoryNode? node = scan.Root.FindDescendant(folder);
            try
            {
                if (!Directory.Exists(folder))
                {
                    if (node is not null)
                    {
                        ScanTreeEditor.ClearContents(node);
                    }

                    continue;
                }

                node ??= ScanTreeEditor.EnsureFolder(scan.Root, folder);
                if (node is null)
                {
                    continue; // der Scan umfasst dieses Laufwerk nicht
                }

                ScanResult fresh = await DiskScanner.ScanAsync(folder);
                ScanTreeEditor.ReplaceContents(node, fresh.Root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = $"Der Papierkorb von {drive.TrimEnd('\\')} ließ sich im Scan nicht nachführen: {ex.Message}. Ein neuer Scan zeigt den aktuellen Stand.";
            }
        }
    }

    private async Task RefreshAfterChangesAsync(ScanResult scan)
    {
        CategoryBreakdown breakdown = await Task.Run(() => CategoryAnalyzer.Analyze(scan.Root));
        if (!ReferenceEquals(scan, _scan))
        {
            return; // inzwischen läuft ein neuer Scan
        }

        _breakdown = breakdown;
        ApplyResult(_scanTarget, _scanDrive, scan, breakdown, refresh: true);
        Places.Refresh();
        _ = Programs.LoadAsync(scan.Root);
        _ = Dev.LoadAsync(scan.Root);
    }

    private void RefreshTopFileMarks()
    {
        foreach (FileRowViewModel row in TopFiles)
        {
            row.RefreshMark();
        }
    }

    private static readonly FileCategory[] PotentialCategories =
    {
        FileCategory.RecycleBin,
        FileCategory.TempAndCaches,
        FileCategory.OldWindows,
    };

    private void UpdatePotential(CategoryBreakdown breakdown)
    {
        PotentialItemViewModel[] items = PotentialCategories
            .Where(c => breakdown[c].SizeOnDisk > 0)
            .Select(c => new PotentialItemViewModel(
                c,
                CategoryCatalog.Get(c).Name,
                CategoryCatalog.Get(c).Brush,
                ByteFormat.Format(breakdown[c].SizeOnDisk),
                breakdown[c].SizeOnDisk))
            .OrderByDescending(i => i.Bytes)
            .ToArray();

        PotentialTotalText = ByteFormat.Format(items.Sum(i => i.Bytes));
        PotentialItems = items;
    }

    private void ExportTopFiles()
    {
        if (_selectedCategory is not FileCategory category)
        {
            return;
        }

        string name = CategoryCatalog.Get(category).Name;
        string[] header = { "Name", "Pfad", "Belegung (Byte)", "Dateigröße (Byte)", "Kategorie", "Zuletzt geändert" };
        IEnumerable<IReadOnlyList<string>> rows = TopFiles.Select(f => (IReadOnlyList<string>)new[]
        {
            f.Name,
            f.FullPath,
            f.SizeBytes.ToString(CultureInfo.InvariantCulture),
            f.LogicalBytes.ToString(CultureInfo.InvariantCulture),
            name,
            f.ModifiedUtc == DateTime.MinValue ? string.Empty : f.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        });

        string topic = "Dateien-" + string.Concat(name.Where(char.IsLetterOrDigit));
        string? message = ExportService.SaveCsv($"Größte Dateien ({name}) exportieren", ExportService.SuggestName(topic, ".csv"), header, rows);
        if (message is not null)
        {
            StatusText = message;
        }
    }

    private void ShowWhere()
    {
        Places.FilterByType = true;
        ViewMode = ViewMode.Places;
    }

    private void ClearResult()
    {
        _breakdown = null;
        _scan = null;
        Basket.Clear();
        Places.Clear();
        Programs.Clear();
        Duplicates.Clear();
        Dev.Clear();
        History.OnScanChanged();
        PotentialItems = Array.Empty<PotentialItemViewModel>();
        PotentialTotalText = string.Empty;
        BarSegments = Array.Empty<BarSegment>();
        BarTotalBytes = 0;
        Rows = Array.Empty<CategoryRowViewModel>();
        SelectedCategory = null;
        HoveredCategory = null;
        TopFiles = Array.Empty<FileRowViewModel>();
        UnattributedText = null;
        IssueText = null;
    }

    private void UpdateTopFiles()
    {
        TopFiles = _breakdown is null || _selectedCategory is not FileCategory category
            ? Array.Empty<FileRowViewModel>()
            : _breakdown.TopFiles(category).Select(f =>
            {
                var row = new FileRowViewModel(f);
                row.AttachBasket(Basket);
                return row;
            }).ToArray();
    }
}
