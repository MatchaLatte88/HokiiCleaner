using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.Core;
using HokiiCleaner.Core.Duplicates;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.App.ViewModels;

public enum DuplicatesState
{
    Idle,
    Running,
    Done,
    Failed,
}

/// <summary>Eine Gruppe identischer Dateien in der Liste links.</summary>
public sealed class DuplicateGroupViewModel
{
    public DuplicateGroupViewModel(DuplicateGroup group, long maxReclaimable, DuplicatesViewModel owner)
    {
        Group = group;
        Owner = owner;
        Name = group.Files[0].File.Name;
        SizeText = ByteFormat.Format(group.SizeOnDisk);
        ReclaimText = ByteFormat.Format(group.ReclaimableBytes);
        CountText = string.Create(CultureInfo.CurrentCulture, $"{group.Files.Count:N0} identische Dateien je {SizeText}");
        ShareValue = maxReclaimable > 0 ? 100.0 * group.ReclaimableBytes / maxReclaimable : 0;
        Files = group.Files.Select(f => new DuplicateFileViewModel(f, this)).ToArray();
    }

    public DuplicateGroup Group { get; }

    public DuplicatesViewModel Owner { get; }

    public string Name { get; }

    public string SizeText { get; }

    /// <summary>Platz, der frei würde, wenn nur eine Kopie bliebe.</summary>
    public string ReclaimText { get; }

    public string CountText { get; }

    public double ShareValue { get; }

    public IReadOnlyList<DuplicateFileViewModel> Files { get; }
}

public sealed class DuplicateFileViewModel : ObservableObject
{
    private bool _isMarked;

    public DuplicateFileViewModel(DuplicateFile file, DuplicateGroupViewModel group)
    {
        File = file;
        Group = group;
        Name = file.File.Name;
        FullPath = file.FullPath;
        DirectoryPath = file.Folder.FullPath;
        ModifiedText = file.File.LastWriteUtc == DateTime.MinValue
            ? "Änderungsdatum unbekannt"
            : "geändert " + file.File.LastWriteUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
        string? why = group.Owner.WhyNotMarkable(FullPath);
        CanMark = why is null;
        MarkToolTip = why ?? "Für den Papierkorb vormerken. Mindestens eine Kopie je Gruppe bleibt.";
        _isMarked = CanMark && group.Owner.IsInBasket(FullPath);
    }

    public DuplicateFile File { get; }

    public DuplicateGroupViewModel Group { get; }

    public string Name { get; }

    public string FullPath { get; }

    public string DirectoryPath { get; }

    public string ModifiedText { get; }

    public bool CanMark { get; }

    public string MarkToolTip { get; }

    /// <summary>Für den Papierkorb vorgemerkt. Wäre es die letzte unmarkierte Kopie der Gruppe, springt der Haken zurück.</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (!CanMark || value == _isMarked)
            {
                Raise();
                return;
            }

            _isMarked = Group.Owner.SetMarked(this, value);
            Raise();
        }
    }

    /// <summary>Gleicht den Haken mit dem Korb ab, wenn sich die Auswahl woanders geändert hat.</summary>
    public void RefreshMark()
    {
        bool marked = CanMark && Group.Owner.IsInBasket(FullPath);
        if (marked != _isMarked)
        {
            _isMarked = marked;
            Raise(nameof(IsMarked));
        }
    }
}

/// <summary>
/// Duplikat-Bericht: findet Dateien mit identischem Inhalt im gewählten Ordner und darunter. Die Suche liest nur.
/// Kopien lassen sich für den Papierkorb vormerken (nie alle einer Gruppe); verschoben wird im Auswahlkorb nach Bestätigung.
/// </summary>
public sealed class DuplicatesViewModel : ObservableObject
{
    private readonly Func<DirectoryNode?> _scopeProvider;
    private readonly Action<DirectoryNode> _showInPlaces;
    private readonly BasketViewModel _basket;
    private CancellationTokenSource? _cts;
    private DuplicatesState _state = DuplicatesState.Idle;
    private long _minMegabytes = 10;
    private string _scopeText = string.Empty;
    private string _progressText = string.Empty;
    private double _fraction;
    private string _summaryText = string.Empty;
    private string? _noteText;
    private string _errorText = string.Empty;
    private IReadOnlyList<DuplicateGroupViewModel> _groups = Array.Empty<DuplicateGroupViewModel>();
    private DuplicateGroupViewModel? _selectedGroup;
    private DuplicateFileViewModel? _selectedFile;
    private DuplicateReport? _report;

    public DuplicatesViewModel(Func<DirectoryNode?> scopeProvider, Action<DirectoryNode> showInPlaces, BasketViewModel basket)
    {
        _scopeProvider = scopeProvider;
        _showInPlaces = showInPlaces;
        _basket = basket;
        _basket.Changed += (_, _) => OnBasketChanged();
        KeepOldestCommand = new RelayCommand(() => KeepOne(KeepRule.Oldest), () => HasGroups && !_basket.IsMoving);
        KeepNewestCommand = new RelayCommand(() => KeepOne(KeepRule.Newest), () => HasGroups && !_basket.IsMoving);
        KeepShortestCommand = new RelayCommand(() => KeepOne(KeepRule.ShortestPath), () => HasGroups && !_basket.IsMoving);
        ClearMarksCommand = new RelayCommand(ClearMarks, () => MarkedCount > 0 && !_basket.IsMoving);
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        RevealCommand = new RelayCommand(Reveal, () => SelectedFile is not null);
        CopyPathCommand = new RelayCommand(CopyPath, () => SelectedFile is not null);
        ShowInFolderCommand = new RelayCommand(() => _showInPlaces(SelectedFile!.File.Folder), () => SelectedFile is not null);
        ExportCommand = new RelayCommand(Export, () => _report is { Groups.Count: > 0 });
    }

    /// <summary>Meldung für die Statuszeile.</summary>
    public Action<string>? Notify { get; set; }

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RevealCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand ShowInFolderCommand { get; }

    public ICommand ExportCommand { get; }

    /// <summary>Markiert in jeder Gruppe alle Kopien außer der ältesten.</summary>
    public ICommand KeepOldestCommand { get; }

    /// <summary>Markiert in jeder Gruppe alle Kopien außer der zuletzt geänderten.</summary>
    public ICommand KeepNewestCommand { get; }

    /// <summary>Markiert in jeder Gruppe alle Kopien außer der mit dem kürzesten Pfad.</summary>
    public ICommand KeepShortestCommand { get; }

    public ICommand ClearMarksCommand { get; }

    public DuplicatesState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value))
            {
                return;
            }

            Raise(nameof(IsRunning));
            Raise(nameof(CanChangeOptions));
            Raise(nameof(ShowIdle));
            Raise(nameof(ShowResult));
            Raise(nameof(ShowGroups));
            Raise(nameof(ShowNoDuplicates));
            Raise(nameof(ShowError));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRunning => State == DuplicatesState.Running;

    /// <summary>Die Mindestgröße lässt sich nur ändern, solange keine Suche läuft.</summary>
    public bool CanChangeOptions => !IsRunning;

    public bool ShowIdle => State == DuplicatesState.Idle;

    public bool ShowResult => State == DuplicatesState.Done;

    /// <summary>Ergebnis mit mindestens einer Gruppe: Listen anzeigen.</summary>
    public bool ShowGroups => ShowResult && HasGroups;

    /// <summary>Ergebnis ohne Treffer: statt leerer Listen ein Hinweis.</summary>
    public bool ShowNoDuplicates => ShowResult && !HasGroups;

    public bool ShowError => State == DuplicatesState.Failed;

    /// <summary>Kleinste verglichene Dateigröße in MB (logisch).</summary>
    public long MinMegabytes
    {
        get => _minMegabytes;
        set
        {
            if (Set(ref _minMegabytes, value))
            {
                Raise(nameof(Min1));
                Raise(nameof(Min10));
                Raise(nameof(Min100));
            }
        }
    }

    public bool Min1
    {
        get => MinMegabytes == 1;
        set => SetMin(value, 1);
    }

    public bool Min10
    {
        get => MinMegabytes == 10;
        set => SetMin(value, 10);
    }

    public bool Min100
    {
        get => MinMegabytes == 100;
        set => SetMin(value, 100);
    }

    private void SetMin(bool selected, long megabytes)
    {
        if (selected)
        {
            MinMegabytes = megabytes;
        }
    }

    /// <summary>Der Ordner, der beim Start durchsucht wird (der in der Orte-Ansicht geöffnete).</summary>
    public string ScopeText
    {
        get => _scopeText;
        private set => Set(ref _scopeText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>Fortschritt in Prozent (0..100).</summary>
    public double Fraction
    {
        get => _fraction;
        private set => Set(ref _fraction, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>Hinweise zu Dateien, die nicht verglichen werden konnten; <c>null</c> = keine.</summary>
    public string? NoteText
    {
        get => _noteText;
        private set
        {
            if (Set(ref _noteText, value))
            {
                Raise(nameof(HasNote));
            }
        }
    }

    public bool HasNote => !string.IsNullOrEmpty(NoteText);

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    public IReadOnlyList<DuplicateGroupViewModel> Groups
    {
        get => _groups;
        private set
        {
            if (Set(ref _groups, value))
            {
                Raise(nameof(HasGroups));
                Raise(nameof(ShowGroups));
                Raise(nameof(ShowNoDuplicates));
            }
        }
    }

    public bool HasGroups => Groups.Count > 0;

    public DuplicateGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (Set(ref _selectedGroup, value))
            {
                Raise(nameof(Files));
                SelectedFile = value?.Files.FirstOrDefault(); // damit die Aktionen sofort greifen
            }
        }
    }

    public IReadOnlyList<DuplicateFileViewModel> Files => SelectedGroup?.Files ?? Array.Empty<DuplicateFileViewModel>();

    public DuplicateFileViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (Set(ref _selectedFile, value))
            {
                Raise(nameof(HasSelectedFile));
            }
        }
    }

    public bool HasSelectedFile => SelectedFile is not null;

    // ------------------------------------------------------------ Markieren für den Papierkorb

    private enum KeepRule
    {
        Oldest,
        Newest,
        ShortestPath,
    }

    private const string MarkSource = "Duplikate";

    internal string? WhyNotMarkable(string path) => _basket.WhyNotMarkable(path);

    internal bool IsInBasket(string path) => _basket.Contains(path);

    /// <summary>Markierte Kopien dieser Ansicht und ihre Belegung.</summary>
    public int MarkedCount => _basket.Items.Count(i => i.Entry.Source == MarkSource);

    public long MarkedBytes => _basket.Items.Where(i => i.Entry.Source == MarkSource).Sum(i => i.SizeBytes);

    public string MarkedText => MarkedCount == 0
        ? "Nichts markiert. Markiere Kopien mit dem Haken oder wähle, welche Kopie je Gruppe bleiben soll."
        : string.Create(CultureInfo.CurrentCulture, $"{MarkedCount:N0} {(MarkedCount == 1 ? "Kopie" : "Kopien")} markiert · {ByteFormat.Format(MarkedBytes)}. Je Gruppe bleibt mindestens eine.");

    /// <summary>Pfade jeder Gruppe, damit der Korb prüfen kann, ob eine Auswahl alle Kopien einer Gruppe verschieben würde.</summary>
    public IReadOnlyList<IReadOnlyList<string>> GroupPaths()
        => _report is null
            ? Array.Empty<IReadOnlyList<string>>()
            : _report.Groups.Select(g => (IReadOnlyList<string>)g.Files.Select(f => f.FullPath).ToArray()).ToArray();

    /// <summary>Markiert oder entmarkiert eine Kopie. Die letzte unmarkierte Kopie einer Gruppe lässt sich nicht markieren.</summary>
    internal bool SetMarked(DuplicateFileViewModel file, bool marked)
    {
        if (!marked)
        {
            return !_basket.Remove(file.FullPath);
        }

        bool anotherStays = file.Group.Files.Any(f => f != file && !_basket.Covers(f.FullPath));
        if (!anotherStays)
        {
            Notify?.Invoke($"Mindestens eine Kopie von „{file.Name}“ muss bleiben.");
            return false;
        }

        return _basket.Add(new BasketEntry(file.FullPath, false, file.Group.Group.SizeOnDisk, 1, MarkSource));
    }

    private void KeepOne(KeepRule rule)
    {
        if (_report is null || _basket.IsMoving)
        {
            return;
        }

        _basket.RemoveRange(Groups.SelectMany(g => g.Files).Select(f => f.FullPath));
        var entries = new List<BasketEntry>();
        foreach (DuplicateGroupViewModel group in Groups)
        {
            DuplicateFileViewModel keeper = ChooseKeeper(group.Files, rule);
            entries.AddRange(group.Files
                .Where(f => f != keeper && f.CanMark)
                .Select(f => new BasketEntry(f.FullPath, false, group.Group.SizeOnDisk, 1, MarkSource)));
        }

        int added = _basket.AddRange(entries);
        Notify?.Invoke(string.Create(
            CultureInfo.CurrentCulture,
            $"{added:N0} Kopien markiert, in jeder Gruppe bleibt eine. Verschoben wird erst nach Bestätigung im Auswahlkorb."));
    }

    private static DuplicateFileViewModel ChooseKeeper(IReadOnlyList<DuplicateFileViewModel> files, KeepRule rule)
    {
        static DateTime Time(DuplicateFileViewModel f) => f.File.File.LastWriteUtc;

        // Dateien ohne bekanntes Änderungsdatum zählen weder als älteste noch als neueste.
        DuplicateFileViewModel[] known = files.Where(f => Time(f) != DateTime.MinValue).ToArray();
        IEnumerable<DuplicateFileViewModel> pool = known.Length > 0 ? known : files;
        return rule switch
        {
            KeepRule.Oldest => pool.OrderBy(Time).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First(),
            KeepRule.Newest => pool.OrderByDescending(Time).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First(),
            _ => files.OrderBy(f => f.FullPath.Length).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).First(),
        };
    }

    private void ClearMarks()
    {
        _basket.RemoveRange(_basket.Items.Where(i => i.Entry.Source == MarkSource).Select(i => i.Path).ToArray());
    }

    private void OnBasketChanged()
    {
        foreach (DuplicateGroupViewModel group in Groups)
        {
            foreach (DuplicateFileViewModel file in group.Files)
            {
                file.RefreshMark();
            }
        }

        Raise(nameof(MarkedCount));
        Raise(nameof(MarkedBytes));
        Raise(nameof(MarkedText));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Nimmt verschobene Dateien aus dem Bericht; Gruppen mit weniger als zwei Kopien fallen weg.</summary>
    public void RemoveFiles(IReadOnlyCollection<string> movedPaths)
    {
        if (_report is null || movedPaths.Count == 0)
        {
            return;
        }

        var moved = new HashSet<string>(movedPaths, StringComparer.OrdinalIgnoreCase);
        var groups = new List<DuplicateGroup>();
        foreach (DuplicateGroup group in _report.Groups)
        {
            DuplicateFile[] remaining = group.Files
                .Where(f => !moved.Contains(f.FullPath) && !IsInsideMoved(f.FullPath, moved))
                .ToArray();
            if (remaining.Length >= 2)
            {
                groups.Add(group with { Files = remaining });
            }
        }

        string? selectedName = SelectedGroup?.Name;
        _report = _report with { Groups = groups };
        long max = groups.Count == 0 ? 0 : groups.Max(g => g.ReclaimableBytes);
        Groups = groups.Select(g => new DuplicateGroupViewModel(g, max, this)).ToArray();
        SelectedGroup = Groups.FirstOrDefault(g => g.Name == selectedName) ?? Groups.FirstOrDefault();
        SummaryText = groups.Count == 0
            ? "Keine identischen Dateien mehr übrig."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{groups.Count:N0} Gruppen identischer Dateien · bis zu {ByteFormat.Format(_report.ReclaimableBytes)} durch Kopien belegt (Stand nach dem Verschieben)");
        OnBasketChanged();
    }

    private static bool IsInsideMoved(string path, HashSet<string> moved)
    {
        for (string? parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (moved.Contains(parent))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Verwirft den Bericht eines früheren Scans.</summary>
    public void Clear()
    {
        _cts?.Cancel();
        _report = null;
        Groups = Array.Empty<DuplicateGroupViewModel>();
        SelectedGroup = null;
        SummaryText = string.Empty;
        NoteText = null;
        ErrorText = string.Empty;
        ProgressText = string.Empty;
        Fraction = 0;
        ScopeText = string.Empty;
        State = DuplicatesState.Idle;
    }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        DirectoryNode? scope = _scopeProvider();
        if (scope is null)
        {
            ErrorText = "Es gibt noch kein Scan-Ergebnis.";
            State = DuplicatesState.Failed;
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _report = null;
        Groups = Array.Empty<DuplicateGroupViewModel>();
        SelectedGroup = null;
        ErrorText = string.Empty;
        NoteText = null;
        ScopeText = scope.FullPath;
        ProgressText = "Sammle Dateien …";
        Fraction = 0;
        State = DuplicatesState.Running;

        var progress = new Progress<DuplicateProgress>(OnProgress);
        try
        {
            DuplicateReport report = await DuplicateFinder.FindAsync(
                scope, new DuplicateOptions { MinLengthBytes = MinMegabytes * 1024 * 1024 }, progress, token);
            Apply(report);
            State = DuplicatesState.Done;
        }
        catch (OperationCanceledException)
        {
            ProgressText = string.Empty;
            State = DuplicatesState.Idle;
            Notify?.Invoke("Duplikat-Suche abgebrochen.");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            State = DuplicatesState.Failed;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(DuplicateProgress p)
    {
        if (State != DuplicatesState.Running)
        {
            return; // Nachzügler nach Ende oder Abbruch
        }

        switch (p.Stage)
        {
            case DuplicateStage.Collecting:
                ProgressText = "Sammle Dateien …";
                break;
            case DuplicateStage.PartialHash:
                ProgressText = string.Create(CultureInfo.CurrentCulture, $"Vergleiche Anfang und Ende · {p.FilesDone:N0} von {p.FilesTotal:N0} Dateien");
                Fraction = p.FilesTotal == 0 ? 0 : 15.0 * p.FilesDone / p.FilesTotal;
                break;
            default:
                ProgressText = $"Prüfe den ganzen Inhalt · {ByteFormat.Format(p.BytesDone)} von {ByteFormat.Format(p.BytesTotal)}";
                Fraction = 15 + (p.BytesTotal == 0 ? 85 : 85.0 * p.BytesDone / p.BytesTotal);
                break;
        }
    }

    private void Apply(DuplicateReport report)
    {
        _report = report;
        long max = report.Groups.Count == 0 ? 0 : report.Groups.Max(g => g.ReclaimableBytes);
        Groups = report.Groups.Select(g => new DuplicateGroupViewModel(g, max, this)).ToArray();
        SelectedGroup = Groups.FirstOrDefault();

        SummaryText = report.Groups.Count == 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Keine identischen Dateien gefunden ({report.CandidateFiles:N0} {(report.CandidateFiles == 1 ? "Datei" : "Dateien")} ab {MinMegabytes} MB verglichen)")
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{report.Groups.Count:N0} Gruppen identischer Dateien · bis zu {ByteFormat.Format(report.ReclaimableBytes)} durch Kopien belegt ({report.CandidateFiles:N0} Dateien verglichen, {report.Elapsed.TotalSeconds:N1} s)");

        var notes = new List<string>();
        if (report.Issues.Count > 0)
        {
            DuplicateIssue first = report.Issues[0];
            notes.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"{report.Issues.Count:N0} Dateien konnten nicht verglichen werden, z. B. {first.Path}: {first.Message}"));
        }

        if (report.SkippedCloudFiles > 0)
        {
            notes.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"{report.SkippedCloudFiles:N0} Dateien liegen nur online (OneDrive) oder sind Verknüpfungen und wurden nicht gelesen."));
        }

        NoteText = notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private void Reveal()
    {
        try
        {
            ExplorerLauncher.Reveal(SelectedFile!.FullPath);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Notify?.Invoke($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    private void CopyPath()
    {
        try
        {
            Clipboard.SetText(SelectedFile!.FullPath);
        }
        catch (ExternalException ex)
        {
            Notify?.Invoke($"Der Pfad konnte nicht kopiert werden: {ex.Message}");
        }
    }

    private void Export()
    {
        if (_report is null)
        {
            return;
        }

        string[] header = { "Gruppe", "Größe (Byte)", "Belegung je Datei (Byte)", "Dateien in der Gruppe", "Vermeidbar (Byte)", "Name", "Pfad", "Zuletzt geändert" };
        var lines = new List<IReadOnlyList<string>>();
        int number = 0;
        foreach (DuplicateGroup group in _report.Groups)
        {
            number++;
            foreach (DuplicateFile file in group.Files)
            {
                lines.Add(new[]
                {
                    number.ToString(CultureInfo.InvariantCulture),
                    group.Length.ToString(CultureInfo.InvariantCulture),
                    group.SizeOnDisk.ToString(CultureInfo.InvariantCulture),
                    group.Files.Count.ToString(CultureInfo.InvariantCulture),
                    group.ReclaimableBytes.ToString(CultureInfo.InvariantCulture),
                    file.File.Name,
                    file.FullPath,
                    file.File.LastWriteUtc == DateTime.MinValue ? string.Empty : file.File.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                });
            }
        }

        string? message = ExportService.SaveCsv("Duplikat-Bericht exportieren", ExportService.SuggestName("Duplikate", ".csv"), header, lines);
        if (message is not null)
        {
            Notify?.Invoke(message);
        }
    }
}
