using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.Core.Diagnostics;

namespace HokiiCleaner.App.ViewModels;

public enum SlownessState
{
    Idle,
    Running,
    Ready,
    Failed,
}

/// <summary>Ein Befund zum Aufklappen: Kopfzeile mit Messwert, darunter Schwelle, Erklärung, Einzelposten und Quelle.</summary>
public sealed class CheckViewModel : ObservableObject
{
    private bool _isExpanded;

    public CheckViewModel(Check check)
    {
        Check = check;
    }

    public Check Check { get; }

    public string Title => Check.Title;

    public string Measurement => Check.Measurement;

    public CheckOutcome Outcome => Check.Outcome;

    public string OutcomeText => Check.Outcome switch
    {
        CheckOutcome.Critical => "KRITISCH",
        CheckOutcome.Warning => "WARNUNG",
        CheckOutcome.Info => "HINWEIS",
        CheckOutcome.Unavailable => "NICHT GEMESSEN",
        _ => string.Empty,
    };

    public string AreaText => Check.Area switch
    {
        CheckArea.Drive => "LAUFWERK",
        CheckArea.Startup => "AUTOSTART",
        CheckArea.Processes => "PROZESSE",
        _ => "SYSTEM",
    };

    public string? Threshold => Check.Threshold is null ? null : "Schwelle: " + Check.Threshold;

    public bool HasThreshold => Check.Threshold is not null;

    public string Explanation => Check.Explanation;

    public string Source => "Quelle: " + Check.Source;

    public IReadOnlyList<string> Details => Check.Details;

    public bool HasDetails => Check.Details.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }
}

/// <summary>Eine Gruppe von Befunden gleicher Schwere; "unauffällig" lässt sich einklappen.</summary>
public sealed class CheckGroupViewModel : ObservableObject
{
    private bool _isOpen;

    public CheckGroupViewModel(string title, IReadOnlyList<CheckViewModel> items, bool isCollapsible)
    {
        Title = title;
        Items = items;
        IsCollapsible = isCollapsible;
        _isOpen = !isCollapsible;
    }

    public string Title { get; }

    public IReadOnlyList<CheckViewModel> Items { get; }

    public string CountText => Items.Count.ToString(CultureInfo.CurrentCulture);

    public bool IsCollapsible { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value))
            {
                Raise(nameof(ShowItems));
            }
        }
    }

    public bool ShowItems => !IsCollapsible || IsOpen;
}

/// <summary>Langsam-Check: misst den Rechner (nur lesend) und zeigt die Befunde nach Schwere gruppiert.</summary>
public sealed class SlownessViewModel : ObservableObject
{
    private static readonly TimeSpan ExpectedExtraTime = TimeSpan.FromSeconds(5);

    private readonly SlownessOptions _options = new();
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private DateTime _startedAt;
    private SlownessState _state = SlownessState.Idle;
    private string _heroNumber = string.Empty;
    private string _heroLabel = string.Empty;
    private string _heroCaption = string.Empty;
    private string? _noteText;
    private string _errorText = string.Empty;
    private string _statusText = string.Empty;
    private double _fraction;
    private IReadOnlyList<CheckGroupViewModel> _groups = Array.Empty<CheckGroupViewModel>();

    private SlownessReport? _lastReport;

    public SlownessViewModel()
    {
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        ExportCommand = new RelayCommand(Export, () => HasResult && _lastReport is not null);
    }

    /// <summary>Speichert den Bericht (Befunde mit Messwert, Schwelle, Erklärung, Quelle) als JSON-Datei.</summary>
    public ICommand ExportCommand { get; }

    private void Export()
    {
        if (_lastReport is null)
        {
            return;
        }

        string json = HokiiCleaner.Core.Export.ReportJson.Serialize(_lastReport, DateTimeOffset.Now);
        string? message = ExportService.SaveJson("Langsam-Check exportieren", ExportService.SuggestName("Langsam-Check", ".json"), json);
        if (message is not null)
        {
            StatusText = message;
        }
    }

    public ICommand RunCommand { get; }

    public ICommand CancelCommand { get; }

    public SlownessState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value))
            {
                return;
            }

            Raise(nameof(IsRunning));
            Raise(nameof(HasResult));
            Raise(nameof(ShowEmpty));
            Raise(nameof(ShowError));
            Raise(nameof(ShowContent));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsRunning => State == SlownessState.Running;

    public bool HasResult => State == SlownessState.Ready;

    public bool ShowEmpty => State == SlownessState.Idle;

    public bool ShowError => State == SlownessState.Failed;

    public bool ShowContent => IsRunning || HasResult;

    /// <summary>Große Zahl der Kopfzeile, z. B. die Anzahl der Befunde, die Aufmerksamkeit brauchen.</summary>
    public string HeroNumber
    {
        get => _heroNumber;
        private set => Set(ref _heroNumber, value);
    }

    public string HeroLabel
    {
        get => _heroLabel;
        private set => Set(ref _heroLabel, value);
    }

    public string HeroCaption
    {
        get => _heroCaption;
        private set => Set(ref _heroCaption, value);
    }

    /// <summary>Hinweis auf Grenzen der Messung (z. B. fehlende Administratorrechte); <c>null</c> = keiner.</summary>
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

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>Fortschritt der Messung in Prozent (0..100); geschätzt aus der verstrichenen Zeit.</summary>
    public double Fraction
    {
        get => _fraction;
        private set => Set(ref _fraction, value);
    }

    public IReadOnlyList<CheckGroupViewModel> Groups
    {
        get => _groups;
        private set => Set(ref _groups, value);
    }

    public async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken cancellationToken = _cts.Token;

        Groups = Array.Empty<CheckGroupViewModel>();
        _lastReport = null;
        ErrorText = string.Empty;
        NoteText = null;
        HeroNumber = "Messe …";
        HeroLabel = string.Empty;
        HeroCaption = string.Empty;
        StatusText = "Die Messung liest nur und verändert nichts.";
        Fraction = 0;
        State = SlownessState.Running;
        StartTimer();

        var progress = new Progress<string>(message =>
        {
            if (State == SlownessState.Running)
            {
                HeroCaption = message;
            }
        });

        try
        {
            SlownessReport report = await SlownessAnalyzer.RunAsync(_options, progress, cancellationToken);
            Apply(report);
            State = SlownessState.Ready;
        }
        catch (OperationCanceledException)
        {
            HeroNumber = string.Empty;
            StatusText = "Messung abgebrochen.";
            State = SlownessState.Idle;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            State = SlownessState.Failed;
        }
        finally
        {
            StopTimer();
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Apply(SlownessReport report)
    {
        _lastReport = report;
        IReadOnlyList<Check> ordered = report.Checks
            .OrderBy(c => c.DisplayRank)
            .ThenBy(c => c.Area)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToArray();

        int critical = ordered.Count(c => c.Outcome == CheckOutcome.Critical);
        int warning = ordered.Count(c => c.Outcome == CheckOutcome.Warning);
        int info = ordered.Count(c => c.Outcome == CheckOutcome.Info);
        int unavailable = ordered.Count(c => c.Outcome == CheckOutcome.Unavailable);
        int ok = ordered.Count(c => c.Outcome == CheckOutcome.Ok);
        int attention = critical + warning;

        var groups = new List<CheckGroupViewModel>();
        AddGroup(groups, "BRAUCHT AUFMERKSAMKEIT", ordered.Where(c => c.NeedsAttention), collapsible: false);
        AddGroup(groups, "HINWEISE", ordered.Where(c => c.Outcome == CheckOutcome.Info), collapsible: false);
        AddGroup(groups, "NICHT GEMESSEN", ordered.Where(c => c.Outcome == CheckOutcome.Unavailable), collapsible: false);
        AddGroup(groups, "UNAUFFÄLLIG", ordered.Where(c => c.Outcome == CheckOutcome.Ok), collapsible: true);

        var parts = new List<string>();
        if (critical > 0)
        {
            parts.Add($"{critical} kritisch");
        }

        if (warning > 0)
        {
            parts.Add(warning == 1 ? "1 Warnung" : $"{warning} Warnungen");
        }

        parts.Add(info == 1 ? "1 Hinweis" : $"{info} Hinweise");
        parts.Add($"{unavailable} nicht messbar");
        parts.Add($"{ok} unauffällig");

        HeroNumber = attention.ToString(CultureInfo.CurrentCulture);
        HeroLabel = attention == 1 ? "Befund braucht Aufmerksamkeit" : "Befunde brauchen Aufmerksamkeit";
        HeroCaption = string.Join(" · ", parts);
        NoteText = report.Snapshot.IsElevated
            ? "Mit Administratorrechten gestartet. S.M.A.R.T. und die Dauer des Systemstarts sind in dieser Version noch nicht umgesetzt."
            : "Ohne Administratorrechte gestartet: S.M.A.R.T., die Dauer des Systemstarts und der Platz der Schattenkopien lassen sich nicht messen, und nicht alle Prozesse sind lesbar.";
        StatusText = string.Create(CultureInfo.CurrentCulture, $"Geprüft in {report.Elapsed.TotalSeconds:N1} s");
        Groups = groups;
    }

    private static void AddGroup(List<CheckGroupViewModel> groups, string title, IEnumerable<Check> checks, bool collapsible)
    {
        CheckViewModel[] items = checks.Select(c => new CheckViewModel(c)).ToArray();
        if (items.Length > 0)
        {
            groups.Add(new CheckGroupViewModel(title, items, collapsible));
        }
    }

    /// <summary>Diagnose: klappt die ersten Befunde aus, die Aufmerksamkeit brauchen, damit Screenshots Details zeigen.</summary>
    public void ExpandFirstFindings(int count)
    {
        foreach (CheckViewModel item in Groups.SelectMany(g => g.Items).Where(i => i.Check.Outcome != CheckOutcome.Ok).Take(count))
        {
            item.IsExpanded = true;
        }
    }

    private void StartTimer()
    {
        _startedAt = DateTime.UtcNow;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            double expected = (_options.SampleWindow + ExpectedExtraTime).TotalSeconds;
            Fraction = Math.Min(95, (DateTime.UtcNow - _startedAt).TotalSeconds / expected * 100);
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        Fraction = 100;
    }
}
