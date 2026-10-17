using System.Globalization;
using System.IO;
using System.Windows.Input;
using HokiiCleaner.Actions;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.Core;

namespace HokiiCleaner.App.ViewModels;

public enum CleanPhase
{
    Idle,
    Analyzing,
    Ready,
    Confirming,
    Cleaning,
    Failed,
}

/// <summary>Ein Aufräum-Ziel mit Haken, Größe, Folge des Löschens und den größten Dateien als Vorschau.</summary>
public sealed class CleanTargetRowViewModel : ObservableObject
{
    private readonly Action<string, bool> _selectionChanged;
    private bool _isSelected;

    public CleanTargetRowViewModel(TargetRun analysis, bool isSelected, Action<string, bool> selectionChanged)
    {
        Analysis = analysis;
        _isSelected = isSelected && CanSelect;
        _selectionChanged = selectionChanged;
    }

    public TargetRun Analysis { get; }

    public CleanTarget Target => Analysis.Target;

    public long Bytes => Analysis.MatchedBytes;

    public long Files => Analysis.MatchedFiles;

    public bool CanSelect => Analysis.Problem is null && Files > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !CanSelect)
            {
                return;
            }

            if (Set(ref _isSelected, value))
            {
                _selectionChanged(Target.Id, value);
            }
        }
    }

    public string Name => Target.Name;

    public string Description => Target.Description;

    public string Consequence => "Folge: " + Target.Consequence;

    public string TierText => Target.Tier == CleanTier.Temp ? "TEMP" : "CACHE";

    public string SizeText => ByteFormat.Format(Bytes);

    public string DetailText
    {
        get
        {
            string files = Files.ToString("N0", CultureInfo.CurrentCulture) + " Dateien";
            return Target.MinAge > TimeSpan.Zero
                ? $"{files} · älter als {Target.MinAge.TotalDays.ToString("0", CultureInfo.CurrentCulture)} Tage"
                : files;
        }
    }

    public string PathsText => string.Join("  ·  ", Target.Roots);

    public IReadOnlyList<string> LargestItems
        => Analysis.Largest.Select(f => $"{ByteFormat.Format(f.Length),10}  {f.Path}").ToArray();

    public bool HasLargest => Analysis.Largest.Count > 0;

    public string? ProblemText => Analysis.Problem;

    public bool HasProblem => Analysis.Problem is not null;
}

public sealed class CleanGroupViewModel
{
    public CleanGroupViewModel(string title, IReadOnlyList<CleanTargetRowViewModel> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }

    public IReadOnlyList<CleanTargetRowViewModel> Rows { get; }
}

/// <summary>
/// Aufräumen: zeigt Temp-Ordner und bekannte Caches mit Größe und Folge, löscht aber erst nach ausdrücklicher Bestätigung
/// und nur die ausgewählten Ziele. Das Löschen ist endgültig (kein Papierkorb).
/// </summary>
public sealed class CleanViewModel : ObservableObject
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HokiiCleaner", "logs");

    private readonly IReadOnlyList<CleanTarget> _targets = CleanTargets.Build(CleanLocations.FromEnvironment());
    private readonly HashSet<string> _selectedIds;
    private readonly Dictionary<string, long> _bytesDoneByTarget = new();
    private CancellationTokenSource? _cts;
    private CleanPhase _phase = CleanPhase.Idle;
    private IReadOnlyList<CleanTargetRowViewModel> _rows = Array.Empty<CleanTargetRowViewModel>();
    private IReadOnlyList<CleanGroupViewModel> _groups = Array.Empty<CleanGroupViewModel>();
    private string _errorText = string.Empty;
    private string _statusText = string.Empty;
    private string _progressText = string.Empty;
    private double _progressFraction;
    private string _confirmText = string.Empty;

    private string _resultTitle = string.Empty;
    private string _resultNote = string.Empty;
    private IReadOnlyList<string> _resultLines = Array.Empty<string>();
    private string? _logPath;

    public CleanViewModel()
    {
        _selectedIds = _targets.Where(t => t.DefaultSelected).Select(t => t.Id).ToHashSet();

        AnalyzeCommand = new RelayCommand(() => _ = AnalyzeAsync(), () => !IsBusy);
        RequestCleanCommand = new RelayCommand(RequestClean, () => Phase == CleanPhase.Ready && SelectedFiles > 0);
        ConfirmCommand = new RelayCommand(() => _ = CleanAsync(), () => Phase == CleanPhase.Confirming);
        CancelConfirmCommand = new RelayCommand(() => Phase = CleanPhase.Ready, () => Phase == CleanPhase.Confirming);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RevealLogCommand = new RelayCommand(RevealLog, () => LogPath is not null);
    }

    public ICommand AnalyzeCommand { get; }

    public ICommand RequestCleanCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelConfirmCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RevealLogCommand { get; }

    public CleanPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value))
            {
                return;
            }

            Raise(nameof(IsBusy));
            Raise(nameof(IsAnalyzing));
            Raise(nameof(IsCleaning));
            Raise(nameof(IsConfirming));
            Raise(nameof(ShowSelectionBar));
            Raise(nameof(ShowList));
            Raise(nameof(ShowError));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsAnalyzing => Phase == CleanPhase.Analyzing;

    public bool IsCleaning => Phase == CleanPhase.Cleaning;

    public bool IsConfirming => Phase == CleanPhase.Confirming;

    public bool IsBusy => Phase is CleanPhase.Analyzing or CleanPhase.Cleaning;

    public bool ShowSelectionBar => Phase is CleanPhase.Ready or CleanPhase.Analyzing;

    public bool ShowList => Phase is CleanPhase.Ready or CleanPhase.Confirming or CleanPhase.Cleaning;

    public bool ShowError => Phase == CleanPhase.Failed;

    public IReadOnlyList<CleanGroupViewModel> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public double ProgressFraction
    {
        get => _progressFraction;
        private set => Set(ref _progressFraction, value);
    }

    public string ConfirmText
    {
        get => _confirmText;
        private set => Set(ref _confirmText, value);
    }

    // Kopfzeile: alles, was gefunden wurde

    public string HeroNumber => ByteFormat.Split(_rows.Sum(r => r.Bytes)).Number;

    public string HeroUnit => ByteFormat.Split(_rows.Sum(r => r.Bytes)).Unit;

    public string HeroCaption => IsAnalyzing
        ? "Prüfe Temp-Ordner und Caches …"
        : _rows.Count == 0
            ? "Noch nicht geprüft."
            : $"in {_rows.Count} Bereichen gefunden. Gelöscht wird endgültig und erst nach deiner Bestätigung.";

    // Auswahl

    public long SelectedBytes => _rows.Where(r => r.IsSelected).Sum(r => r.Bytes);

    public long SelectedFiles => _rows.Where(r => r.IsSelected).Sum(r => r.Files);

    public string SelectionText
    {
        get
        {
            if (SelectedFiles == 0)
            {
                return "Nichts ausgewählt.";
            }

            return $"{ByteFormat.Format(SelectedBytes)} in {SelectedFiles.ToString("N0", CultureInfo.CurrentCulture)} Dateien ausgewählt";
        }
    }

    // Ergebnis des letzten Löschlaufs

    public bool HasResult => ResultTitle.Length > 0;

    public string ResultTitle
    {
        get => _resultTitle;
        private set
        {
            if (Set(ref _resultTitle, value))
            {
                Raise(nameof(HasResult));
            }
        }
    }

    public string ResultNote
    {
        get => _resultNote;
        private set => Set(ref _resultNote, value);
    }

    public IReadOnlyList<string> ResultLines
    {
        get => _resultLines;
        private set => Set(ref _resultLines, value);
    }

    public string? LogPath
    {
        get => _logPath;
        private set
        {
            if (Set(ref _logPath, value))
            {
                Raise(nameof(HasLog));
            }
        }
    }

    public bool HasLog => LogPath is not null;

    /// <summary>Prüft beim ersten Aufruf des Bereichs; danach nur auf Wunsch.</summary>
    public void EnsureAnalyzed()
    {
        if (Phase == CleanPhase.Idle)
        {
            _ = AnalyzeAsync();
        }
    }

    public async Task AnalyzeAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Phase = CleanPhase.Analyzing;
        ErrorText = string.Empty;
        StatusText = "Prüfe Temp-Ordner und Caches …";
        RaiseHero();

        try
        {
            DateTime now = DateTime.UtcNow;
            TargetRun[] runs = await Task.WhenAll(_targets.Select(t => Task.Run(() => CleanEngine.Analyze(t, now, ct), ct)));
            if (runs.Any(r => r.Cancelled))
            {
                Phase = CleanPhase.Idle;
                StatusText = "Prüfung abgebrochen.";
                return;
            }

            // Ziele, die es auf diesem Rechner nicht gibt, werden nicht angezeigt; abgelehnte schon (mit Begründung).
            _rows = runs.Where(r => !r.Missing)
                .Select(r => new CleanTargetRowViewModel(r, _selectedIds.Contains(r.Target.Id), OnSelectionChanged))
                .ToArray();
            Groups = new[]
            {
                new CleanGroupViewModel("TEMP-DATEIEN", _rows.Where(r => r.Target.Tier == CleanTier.Temp).ToArray()),
                new CleanGroupViewModel("PROGRAMM-CACHES", _rows.Where(r => r.Target.Tier == CleanTier.Cache).ToArray()),
            }.Where(g => g.Rows.Count > 0).ToArray();

            Phase = CleanPhase.Ready;
            StatusText = "Geprüft. Es wurde nichts verändert.";
        }
        catch (OperationCanceledException)
        {
            Phase = CleanPhase.Idle;
            StatusText = "Prüfung abgebrochen.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Phase = CleanPhase.Failed;
            StatusText = string.Empty;
        }
        finally
        {
            RaiseHero();
            RaiseSelection();
        }
    }

    private void OnSelectionChanged(string id, bool selected)
    {
        if (selected)
        {
            _selectedIds.Add(id);
        }
        else
        {
            _selectedIds.Remove(id);
        }

        RaiseSelection();
    }

    private void RaiseSelection()
    {
        Raise(nameof(SelectedBytes));
        Raise(nameof(SelectedFiles));
        Raise(nameof(SelectionText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseHero()
    {
        Raise(nameof(HeroNumber));
        Raise(nameof(HeroUnit));
        Raise(nameof(HeroCaption));
    }

    private void RequestClean()
    {
        CleanTargetRowViewModel[] selected = _rows.Where(r => r.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        string names = string.Join(", ", selected.Select(r => r.Name));
        ConfirmText = $"{ByteFormat.Format(SelectedBytes)} in {SelectedFiles.ToString("N0", CultureInfo.CurrentCulture)} Dateien endgültig löschen? "
            + $"Betroffen: {names}. Das lässt sich nicht rückgängig machen und landet nicht im Papierkorb.";
        Phase = CleanPhase.Confirming;
    }

    private async Task CleanAsync()
    {
        if (Phase != CleanPhase.Confirming)
        {
            return;
        }

        CleanTarget[] targets = _rows.Where(r => r.IsSelected).Select(r => r.Target).ToArray();
        long plannedBytes = Math.Max(1, SelectedBytes);
        _bytesDoneByTarget.Clear();
        ProgressFraction = 0;
        ProgressText = "Starte …";
        ClearResult();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        Phase = CleanPhase.Cleaning;
        StatusText = "Lösche …";

        var progress = new Progress<CleanProgress>(p =>
        {
            _bytesDoneByTarget[p.TargetName] = p.BytesProcessed;
            ProgressFraction = Math.Min(1, (double)_bytesDoneByTarget.Values.Sum() / plannedBytes);
            ProgressText = $"{p.TargetName}: {p.FilesProcessed.ToString("N0", CultureInfo.CurrentCulture)} Dateien bearbeitet";
        });

        try
        {
            CleanResult result = await Task.Run(() => CleanEngine.CleanAll(targets, progress, ct));
            ShowResult(result);
            StatusText = result.Cancelled ? "Abgebrochen. Bereits Gelöschtes bleibt gelöscht." : "Fertig.";
        }
        catch (Exception ex)
        {
            ErrorText = "Beim Löschen ist ein Fehler aufgetreten: " + ex.Message;
            Phase = CleanPhase.Failed;
            return;
        }

        Phase = CleanPhase.Idle;
        await AnalyzeAsync();
    }

    private void ClearResult()
    {
        ResultTitle = string.Empty;
        ResultNote = string.Empty;
        ResultLines = Array.Empty<string>();
        LogPath = null;
    }

    private void ShowResult(CleanResult result)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ResultTitle = result.FreedBytesMeasured is long freed && freed > 0
            ? $"{ByteFormat.Format(freed)} mehr frei"
            : result.FreedBytesMeasured is null
                ? "Freier Platz nicht messbar"
                : "Kein Zuwachs an freiem Platz gemessen";

        ResultNote = $"Gelöscht laut Dateigrößen: {ByteFormat.Format(result.DeletedBytes)} in {result.DeletedFiles.ToString("N0", culture)} Dateien. "
            + "Der Zuwachs ist am freien Platz der Laufwerke gemessen; andere Programme oder geteilte Hardlinks können ihn verändern."
            + (result.Cancelled ? " Der Lauf wurde abgebrochen." : string.Empty);

        ResultLines = result.Runs.Select(DescribeRun).ToArray();

        try
        {
            LogPath = CleanLog.Write(LogDirectory, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResultNote += " Das Protokoll konnte nicht geschrieben werden: " + ex.Message;
        }
    }

    private static string DescribeRun(TargetRun run)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string text = $"{run.Target.Name}: {ByteFormat.Format(run.DeletedBytes)} in {run.DeletedFiles.ToString("N0", culture)} Dateien gelöscht";
        if (run.SkippedTotal > 0)
        {
            string reasons = string.Join(", ", run.SkippedCounts.OrderBy(p => p.Key).Select(p => $"{p.Value.ToString("N0", culture)} {ReasonText(p.Key)}"));
            text += $"; übersprungen: {reasons}";
        }

        return run.Problem is null ? text : text + $"; abgelehnt: {run.Problem}";
    }

    private static string ReasonText(SkipReason reason) => reason switch
    {
        SkipReason.InUse => "in Benutzung",
        SkipReason.AccessDenied => "kein Zugriff",
        SkipReason.UnreadableFolder => "Ordner nicht lesbar",
        _ => "Fehler",
    };

    private void RevealLog()
    {
        if (LogPath is null)
        {
            return;
        }

        try
        {
            ExplorerLauncher.Reveal(LogPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = $"Explorer konnte nicht geöffnet werden: {ex.Message}";
        }
    }
}
