using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.Core;
using HokiiCleaner.Core.History;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.App.ViewModels;

/// <summary>Ein gespeicherter Stand in der Liste.</summary>
public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(SnapshotInfo info, string? currentRoot)
    {
        Info = info;
        TakenText = info.TakenUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        RootPath = info.RootPath;
        SizeText = ByteFormat.Format(info.TotalSizeOnDisk);
        MatchesCurrent = currentRoot is not null
            && string.Equals(info.RootPath.TrimEnd('\\'), currentRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        DetailText = string.Create(CultureInfo.CurrentCulture, $"{RootPath} · Datei {ByteFormat.Format(info.FileBytes)}");
    }

    public SnapshotInfo Info { get; }

    public string TakenText { get; }

    public string RootPath { get; }

    public string SizeText { get; }

    public string DetailText { get; }

    /// <summary>Gehört zum Ordner des aktuellen Scans und lässt sich damit vergleichen.</summary>
    public bool MatchesCurrent { get; }
}

/// <summary>Eine Zeile des Vergleichs: ein Ordner mit Größe vorher, nachher und Änderung.</summary>
public sealed class HistoryChangeViewModel
{
    public HistoryChangeViewModel(FolderChange change, long maxAbsolute, long minFolderBytes)
    {
        Change = change;
        Path = change.Path;
        OldText = change.OldBytes == 0 ? "unter " + ByteFormat.Format(minFolderBytes) : ByteFormat.Format(change.OldBytes);
        NewText = change.NewBytes == 0 ? "unter " + ByteFormat.Format(minFolderBytes) : ByteFormat.Format(change.NewBytes);
        IsGrowth = change.Delta > 0;
        DeltaText = (IsGrowth ? "+" : "−") + ByteFormat.Format(Math.Abs(change.Delta));
        ShareValue = maxAbsolute == 0 ? 0 : 100.0 * Math.Abs(change.Delta) / maxAbsolute;
    }

    public FolderChange Change { get; }

    public string Path { get; }

    public string OldText { get; }

    public string NewText { get; }

    public string DeltaText { get; }

    public bool IsGrowth { get; }

    public double ShareValue { get; }
}

/// <summary>
/// Verlauf: speichert auf Wunsch den aktuellen Scan als Stand (Ordner ab 50 MB, keine Dateien) und vergleicht ihn später mit einem
/// neuen Scan desselben Ordners. Geschrieben wird nur beim Klick auf "Stand speichern" und nur in den eigenen Ordner von HokiiCleaner.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private static readonly string HistoryFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HokiiCleaner", "history");

    private readonly Func<DirectoryNode?> _currentRoot;
    private IReadOnlyList<HistoryItemViewModel> _items = Array.Empty<HistoryItemViewModel>();
    private HistoryItemViewModel? _selected;
    private IReadOnlyList<HistoryChangeViewModel> _changes = Array.Empty<HistoryChangeViewModel>();
    private SnapshotComparison? _comparison;
    private bool _isBusy;
    private string _summaryText = "Noch kein Stand gespeichert.";
    private string _noteText = string.Empty;
    private string _errorText = string.Empty;
    private string _resultText = string.Empty;
    private bool _isConfirmingDelete;
    private bool _loadedOnce;

    public HistoryViewModel(Func<DirectoryNode?> currentRoot)
    {
        _currentRoot = currentRoot;
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsBusy && _currentRoot() is not null);
        CompareCommand = new RelayCommand(() => _ = CompareAsync(), () => !IsBusy && _currentRoot() is not null && SelectedItem is { MatchesCurrent: true });
        RequestDeleteCommand = new RelayCommand(() => IsConfirmingDelete = true, () => !IsBusy && SelectedItem is not null && !IsConfirmingDelete);
        ConfirmDeleteCommand = new RelayCommand(DeleteSelected, () => IsConfirmingDelete && SelectedItem is not null);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false, () => IsConfirmingDelete);
        ExportCommand = new RelayCommand(Export, () => _comparison is { Changes.Count: > 0 });
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    /// <summary>Meldung für die Statuszeile.</summary>
    public Action<string>? Notify { get; set; }

    public ICommand SaveCommand { get; }

    public ICommand CompareCommand { get; }

    public ICommand RequestDeleteCommand { get; }

    public ICommand ConfirmDeleteCommand { get; }

    public ICommand CancelDeleteCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public IReadOnlyList<HistoryItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (Set(ref _items, value))
            {
                Raise(nameof(HasItems));
                Raise(nameof(ShowNoItems));
            }
        }
    }

    public bool HasItems => Items.Count > 0;

    public bool ShowNoItems => !HasItems;

    public HistoryItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                IsConfirmingDelete = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public IReadOnlyList<HistoryChangeViewModel> Changes
    {
        get => _changes;
        private set
        {
            if (Set(ref _changes, value))
            {
                Raise(nameof(HasChanges));
            }
        }
    }

    public bool HasChanges => Changes.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        private set
        {
            if (Set(ref _isConfirmingDelete, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string NoteText
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

    public bool HasNote => NoteText.Length > 0;

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

    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (Set(ref _resultText, value))
            {
                Raise(nameof(HasResult));
                Raise(nameof(ShowCompareHint));
            }
        }
    }

    public bool HasResult => ResultText.Length > 0;

    /// <summary>Solange noch nichts verglichen wurde, steht rechts eine Anleitung.</summary>
    public bool ShowCompareHint => !HasResult;

    /// <summary>Hinweis, ob ein aktueller Scan vorliegt und welchen Ordner er betrifft.</summary>
    public string CurrentScanText
    {
        get
        {
            DirectoryNode? root = _currentRoot();
            return root is null
                ? "Kein aktueller Scan. Zum Speichern oder Vergleichen zuerst im Bereich Speicher analysieren."
                : $"Aktueller Scan: {root.FullPath}";
        }
    }

    /// <summary>Liest die Liste beim ersten Aufruf; danach nur auf Wunsch oder wenn sich der Scan ändert.</summary>
    public void EnsureLoaded()
    {
        if (!_loadedOnce)
        {
            LoadList();
        }
    }

    /// <summary>Der aktuelle Scan hat gewechselt: Anzeige und Vergleich zurücksetzen.</summary>
    public void OnScanChanged()
    {
        Changes = Array.Empty<HistoryChangeViewModel>();
        _comparison = null;
        ResultText = string.Empty;
        ErrorText = string.Empty;
        Raise(nameof(CurrentScanText));
        if (_loadedOnce)
        {
            LoadList();
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadList()
    {
        _loadedOnce = true;
        IReadOnlyList<SnapshotInfo> infos = SnapshotStore.List(HistoryFolder, out IReadOnlyList<string> problems);
        string? currentRoot = _currentRoot()?.FullPath;
        string? selectedPath = SelectedItem?.Info.FilePath;
        HistoryItemViewModel[] items = infos.Select(i => new HistoryItemViewModel(i, currentRoot)).ToArray();
        Items = items;
        SelectedItem = items.FirstOrDefault(i => i.Info.FilePath == selectedPath)
            ?? items.FirstOrDefault(i => i.MatchesCurrent)
            ?? items.FirstOrDefault();
        SummaryText = items.Length == 0
            ? "Noch kein Stand gespeichert."
            : string.Create(CultureInfo.CurrentCulture, $"{items.Length:N0} {(items.Length == 1 ? "Stand" : "Stände")} gespeichert");
        NoteText = problems.Count == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"{problems.Count:N0} Dateien im Ordner ließen sich nicht lesen, zum Beispiel: {problems[0]}");
        Raise(nameof(CurrentScanText));
    }

    private async Task SaveAsync()
    {
        DirectoryNode? root = _currentRoot();
        if (root is null)
        {
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            string path = await Task.Run(() => SnapshotStore.Save(HistoryFolder, SnapshotBuilder.Build(root, DateTime.UtcNow)));
            LoadList();
            SelectedItem = Items.FirstOrDefault(i => i.Info.FilePath == path) ?? SelectedItem;
            ResultText = $"Stand gespeichert: {path}";
            Notify?.Invoke("Stand gespeichert. Nach einem späteren Scan lässt er sich mit dem neuen Stand vergleichen.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = $"Der Stand konnte nicht gespeichert werden: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompareAsync()
    {
        DirectoryNode? root = _currentRoot();
        if (root is null || SelectedItem is not { MatchesCurrent: true } item)
        {
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            SnapshotComparison comparison = await Task.Run(() =>
                SnapshotComparer.Compare(SnapshotStore.Load(item.Info.FilePath), SnapshotBuilder.Build(root, DateTime.UtcNow)));
            _comparison = comparison;
            long max = comparison.Changes.Count == 0 ? 0 : comparison.Changes.Max(c => Math.Abs(c.Delta));
            Changes = comparison.Changes.Select(c => new HistoryChangeViewModel(c, max, comparison.Old.MinFolderBytes)).ToArray();
            string direction = comparison.TotalDelta >= 0 ? "gewachsen" : "geschrumpft";
            string tail = Changes.Count == 0
                ? "Keine einzelne Änderung ab 20 MB."
                : $"Größte Veränderungen unten (nur Ordner ab {ByteFormat.Format(comparison.Old.MinFolderBytes)}; Kleineres bleibt unsichtbar).";
            ResultText = string.Create(
                CultureInfo.CurrentCulture,
                $"Seit {item.TakenText} ist {root.FullPath} um {ByteFormat.Format(Math.Abs(comparison.TotalDelta))} {direction}. {tail}");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ErrorText = $"Der Vergleich ist fehlgeschlagen: {ex.Message}";
            Changes = Array.Empty<HistoryChangeViewModel>();
            _comparison = null;
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void DeleteSelected()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        try
        {
            File.Delete(item.Info.FilePath);
            ResultText = $"Stand vom {item.TakenText} gelöscht.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = $"Der Stand konnte nicht gelöscht werden: {ex.Message}";
        }

        IsConfirmingDelete = false;
        LoadList();
    }

    private void Export()
    {
        if (_comparison is null)
        {
            return;
        }

        string[] header = { "Ordner", "Vorher (Byte)", "Nachher (Byte)", "Änderung (Byte)" };
        IEnumerable<IReadOnlyList<string>> lines = _comparison.Changes.Select(c => (IReadOnlyList<string>)new[]
        {
            c.Path,
            c.OldBytes.ToString(CultureInfo.InvariantCulture),
            c.NewBytes.ToString(CultureInfo.InvariantCulture),
            c.Delta.ToString(CultureInfo.InvariantCulture),
        });

        string? message = ExportService.SaveCsv("Vergleich exportieren", ExportService.SuggestName("Verlauf", ".csv"), header, lines);
        if (message is not null)
        {
            Notify?.Invoke(message);
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(HistoryFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{HistoryFolder}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Notify?.Invoke($"Ordner konnte nicht geöffnet werden: {ex.Message}");
        }
    }
}
