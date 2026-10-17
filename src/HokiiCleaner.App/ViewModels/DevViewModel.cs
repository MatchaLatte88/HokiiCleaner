using System.Globalization;
using System.Windows.Input;
using HokiiCleaner.App.Infrastructure;
using HokiiCleaner.Core;
using HokiiCleaner.Core.Dev;
using HokiiCleaner.Core.Scanning;

namespace HokiiCleaner.App.ViewModels;

/// <summary>Eine Zeile der Entwickler-Funde: ein neu erzeugbarer Ordner mit Haken für den Papierkorb.</summary>
public sealed class DevRowViewModel : ObservableObject
{
    private readonly BasketViewModel _basket;
    private bool _isMarked;

    public DevRowViewModel(DevArtifact artifact, BasketViewModel basket)
    {
        Artifact = artifact;
        _basket = basket;
        Title = $"{artifact.Project.Name} · {artifact.Title}";
        FullPath = artifact.FullPath;
        SizeText = ByteFormat.Format(artifact.SizeOnDisk);
        DetailText = string.Create(
            CultureInfo.CurrentCulture,
            $"{artifact.FileCount:N0} Dateien · {ActivityText(artifact.ProjectActivityUtc)}");
        string? why = basket.WhyNotMarkable(FullPath);
        CanMark = why is null;
        MarkToolTip = why ?? "Für den Papierkorb vormerken (verschoben wird erst nach Bestätigung)";
        _isMarked = CanMark && basket.Contains(FullPath);
    }

    public DevArtifact Artifact { get; }

    public string Title { get; }

    public string FullPath { get; }

    public string SizeText { get; }

    public string DetailText { get; }

    public string Regenerate => Artifact.Regenerate;

    public string? Caution => Artifact.Caution;

    public bool HasCaution => Caution is not null;

    public bool CanMark { get; }

    public string MarkToolTip { get; }

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

            _isMarked = value
                ? _basket.Add(new BasketEntry(FullPath, true, Artifact.SizeOnDisk, Artifact.FileCount, "Entwickler"))
                : !_basket.Remove(FullPath);
            Raise();
        }
    }

    public void RefreshMark()
    {
        bool marked = CanMark && _basket.Contains(FullPath);
        if (marked != _isMarked)
        {
            _isMarked = marked;
            Raise(nameof(IsMarked));
        }
    }

    private static string ActivityText(DateTime utc)
    {
        if (utc == DateTime.MinValue)
        {
            return "Projekt ohne weitere Dateien";
        }

        int days = (int)(DateTime.UtcNow - utc).TotalDays;
        return days < 1
            ? "Projekt heute geändert"
            : string.Create(CultureInfo.CurrentCulture, $"Projekt seit {days:N0} {(days == 1 ? "Tag" : "Tagen")} unverändert");
    }
}

/// <summary>
/// Entwickler-Funde: Ordner wie <c>node_modules</c>, Python-Umgebungen oder Build-Ausgaben, die ihr Projekt neu erzeugen kann.
/// Die Suche liest nur den Scan im Speicher. Vorgemerkt wird für den Papierkorb im Auswahlkorb, nie automatisch.
/// </summary>
public sealed class DevViewModel : ObservableObject
{
    private readonly BasketViewModel _basket;
    private int _version;
    private IReadOnlyList<DevArtifact> _all = Array.Empty<DevArtifact>();
    private IReadOnlyList<DevRowViewModel> _rows = Array.Empty<DevRowViewModel>();
    private int _minIdleDays = 90;
    private bool _isBusy;
    private string _summaryText = string.Empty;
    private string _errorText = string.Empty;

    public DevViewModel(BasketViewModel basket)
    {
        _basket = basket;
        _basket.Changed += (_, _) => RefreshMarks();
        MarkAllCommand = new RelayCommand(MarkAll, () => Rows.Any(r => r.CanMark) && !_basket.IsMoving);
        ClearMarksCommand = new RelayCommand(ClearMarks, () => Rows.Any(r => r.IsMarked) && !_basket.IsMoving);
    }

    /// <summary>Markiert alle gezeigten Ordner (die sich markieren lassen).</summary>
    public ICommand MarkAllCommand { get; }

    public ICommand ClearMarksCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
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

    public IReadOnlyList<DevRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (Set(ref _rows, value))
            {
                Raise(nameof(HasRows));
                Raise(nameof(ShowEmpty));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    public bool ShowEmpty => !IsBusy && !HasRows && !HasError;

    public int MinIdleDays
    {
        get => _minIdleDays;
        set
        {
            if (Set(ref _minIdleDays, value))
            {
                Raise(nameof(Idle0));
                Raise(nameof(Idle30));
                Raise(nameof(Idle90));
                Raise(nameof(Idle180));
                Raise(nameof(Idle365));
                Rebuild();
            }
        }
    }

    public bool Idle0
    {
        get => MinIdleDays == 0;
        set => SetIdle(value, 0);
    }

    public bool Idle30
    {
        get => MinIdleDays == 30;
        set => SetIdle(value, 30);
    }

    public bool Idle90
    {
        get => MinIdleDays == 90;
        set => SetIdle(value, 90);
    }

    public bool Idle180
    {
        get => MinIdleDays == 180;
        set => SetIdle(value, 180);
    }

    public bool Idle365
    {
        get => MinIdleDays == 365;
        set => SetIdle(value, 365);
    }

    private void SetIdle(bool selected, int days)
    {
        if (selected)
        {
            MinIdleDays = days;
        }
    }

    public void Clear()
    {
        _version++;
        _all = Array.Empty<DevArtifact>();
        Rows = Array.Empty<DevRowViewModel>();
        SummaryText = string.Empty;
        ErrorText = string.Empty;
        IsBusy = false;
        Raise(nameof(ShowEmpty));
    }

    /// <summary>Sucht im Scan nach neu erzeugbaren Ordnern.</summary>
    public async Task LoadAsync(DirectoryNode root)
    {
        int version = ++_version;
        IsBusy = true;
        ErrorText = string.Empty;
        SummaryText = "Suche …";
        Raise(nameof(ShowEmpty));
        IReadOnlyList<DevArtifact> found;
        try
        {
            found = await Task.Run(() => DevArtifactFinder.Find(root, CancellationToken.None));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            if (version == _version)
            {
                ErrorText = $"Die Suche ist fehlgeschlagen: {ex.Message}";
                IsBusy = false;
            }

            return;
        }

        if (version != _version)
        {
            return;
        }

        _all = found;
        IsBusy = false;
        Rebuild();
    }

    private void Rebuild()
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(MinIdleDays);
        DevRowViewModel[] rows = _all
            .Where(a => MinIdleDays == 0 || a.ProjectActivityUtc < cutoff)
            .OrderByDescending(a => a.SizeOnDisk)
            .Select(a => new DevRowViewModel(a, _basket))
            .ToArray();
        Rows = rows;

        long shown = rows.Sum(r => r.Artifact.SizeOnDisk);
        long all = _all.Sum(a => a.SizeOnDisk);
        SummaryText = _all.Count == 0
            ? "Keine neu erzeugbaren Projektordner gefunden."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{rows.Length:N0} von {_all.Count:N0} Ordnern gezeigt · {ByteFormat.Format(shown)} (alle zusammen {ByteFormat.Format(all)})");
        Raise(nameof(ShowEmpty));
        CommandManager.InvalidateRequerySuggested();
    }

    private void MarkAll()
    {
        _basket.AddRange(Rows.Where(r => r.CanMark).Select(r => new BasketEntry(r.FullPath, true, r.Artifact.SizeOnDisk, r.Artifact.FileCount, "Entwickler")));
    }

    private void ClearMarks()
    {
        _basket.RemoveRange(Rows.Where(r => r.IsMarked).Select(r => r.FullPath).ToArray());
    }

    private void RefreshMarks()
    {
        foreach (DevRowViewModel row in Rows)
        {
            row.RefreshMark();
        }

        CommandManager.InvalidateRequerySuggested();
    }
}
