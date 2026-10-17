using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Cleanii.Actions;
using Cleanii.App.Infrastructure;
using Cleanii.Core;

namespace Cleanii.App.ViewModels;

/// <summary>Ein Eintrag im Papierkorb mit Wiederherstellen.</summary>
public sealed class BinRowViewModel : ObservableObject
{
    private string _sizeText;

    public BinRowViewModel(BinEntry entry)
    {
        Entry = entry;
        Name = entry.Name;
        OriginalFolder = Path.GetDirectoryName(entry.OriginalPath.TrimEnd('\\')) ?? entry.OriginalPath;
        DeletedText = entry.DeletedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        KindText = entry.IsDirectory ? "Ordner" : "Datei";
        _sizeText = entry.SizeBytes is long bytes ? ByteFormat.Format(bytes) : "wird gemessen …";
        SizeBytes = entry.SizeBytes ?? 0;
    }

    public BinEntry Entry { get; }

    public string Name { get; }

    public string OriginalFolder { get; }

    public string DeletedText { get; }

    public string KindText { get; }

    public long SizeBytes { get; private set; }

    public string SizeText
    {
        get => _sizeText;
        private set => Set(ref _sizeText, value);
    }

    public void SetMeasured(long bytes, int unreadableFolders)
    {
        SizeBytes = bytes;
        SizeText = unreadableFolders > 0 ? ByteFormat.Format(bytes) + " +" : ByteFormat.Format(bytes);
    }

    public void SetUnmeasurable() => SizeText = "unbekannt";
}

public enum BinPhase
{
    Idle,
    Loading,
    ConfirmingEmpty,
    Working,
}

/// <summary>
/// Papierkorb-Ansicht: zeigt Größe und Inhalt der Papierkörbe (feste Laufwerke), stellt Einträge am ursprünglichen Ort wieder
/// her und leert den Papierkorb nach Bestätigung. Leeren ist endgültig; deshalb gibt es eine eigene Bestätigung.
/// </summary>
public sealed class BinViewModel : ObservableObject
{
    private readonly Func<Task> _binChanged;
    private int _version;
    private BinPhase _phase = BinPhase.Idle;
    private IReadOnlyList<BinRowViewModel> _rows = Array.Empty<BinRowViewModel>();
    private IReadOnlyList<BinDriveStatus> _drives = Array.Empty<BinDriveStatus>();
    private string _summaryText = "Der Papierkorb wird gelesen …";
    private string _noteText = string.Empty;
    private string _confirmText = string.Empty;
    private string _resultText = string.Empty;
    private bool _loadedOnce;

    public BinViewModel(Func<Task> binChanged)
    {
        _binChanged = binChanged;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => Phase is BinPhase.Idle);
        RestoreCommand = new RelayCommand<BinRowViewModel>(row => _ = RestoreAsync(row), _ => Phase == BinPhase.Idle);
        RequestEmptyCommand = new RelayCommand(RequestEmpty, () => Phase == BinPhase.Idle && TotalItems > 0);
        ConfirmEmptyCommand = new RelayCommand(() => _ = EmptyAsync(), () => Phase == BinPhase.ConfirmingEmpty);
        CancelEmptyCommand = new RelayCommand(() => Phase = BinPhase.Idle, () => Phase == BinPhase.ConfirmingEmpty);
        OpenInExplorerCommand = new RelayCommand(OpenInExplorer);
    }

    /// <summary>Meldung für die Statuszeile.</summary>
    public Action<string>? Notify { get; set; }

    public ICommand RefreshCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand RequestEmptyCommand { get; }

    public ICommand ConfirmEmptyCommand { get; }

    public ICommand CancelEmptyCommand { get; }

    public ICommand OpenInExplorerCommand { get; }

    public BinPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value))
            {
                return;
            }

            Raise(nameof(IsBusy));
            Raise(nameof(IsConfirmingEmpty));
            Raise(nameof(ShowActions));
            Raise(nameof(ShowEmptyHint));
            Raise(nameof(NeedsBar));
            Raise(nameof(ShowResultOnly));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy => Phase is BinPhase.Loading or BinPhase.Working;

    public bool IsConfirmingEmpty => Phase == BinPhase.ConfirmingEmpty;

    public bool ShowActions => Phase is BinPhase.Idle or BinPhase.Loading;

    /// <summary>Hinweis statt leerer Liste, wenn nichts im Papierkorb liegt und nicht gerade gelesen wird.</summary>
    public bool ShowEmptyHint => !HasRows && Phase != BinPhase.Loading;

    /// <summary>Die Aktionsleiste unten hat etwas zu zeigen (Bestätigung oder Ergebnis).</summary>
    public bool NeedsBar => IsConfirmingEmpty || HasResult;

    public bool ShowResultOnly => !IsConfirmingEmpty && HasResult;

    public IReadOnlyList<BinRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (Set(ref _rows, value))
            {
                Raise(nameof(HasRows));
                Raise(nameof(ShowEmptyHint));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    public IReadOnlyList<BinDriveStatus> Drives
    {
        get => _drives;
        private set
        {
            if (Set(ref _drives, value))
            {
                Raise(nameof(TotalItems));
                Raise(nameof(TotalBytes));
            }
        }
    }

    public long TotalItems => Drives.Sum(d => d.Items);

    public long TotalBytes => Drives.Sum(d => d.Bytes);

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>Hinweise zur Liste (nicht lesbare Einträge, Größe laut Windows); leer, wenn keine.</summary>
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

    public string ConfirmText
    {
        get => _confirmText;
        private set => Set(ref _confirmText, value);
    }

    /// <summary>Ergebnis der letzten Aktion (Leeren, Wiederherstellen).</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (Set(ref _resultText, value))
            {
                Raise(nameof(HasResult));
                Raise(nameof(NeedsBar));
                Raise(nameof(ShowResultOnly));
            }
        }
    }

    public bool HasResult => ResultText.Length > 0;

    /// <summary>Der Papierkorb hat sich außerhalb dieser Ansicht geändert: beim nächsten Öffnen neu lesen.</summary>
    public void MarkStale() => _loadedOnce = false;

    /// <summary>Liest beim ersten Aufruf; danach nur auf Wunsch.</summary>
    public void EnsureLoaded()
    {
        if (!_loadedOnce && Phase == BinPhase.Idle)
        {
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (Phase is BinPhase.Loading or BinPhase.Working)
        {
            return;
        }

        int version = ++_version;
        _loadedOnce = true;
        Phase = BinPhase.Loading;
        SummaryText = "Der Papierkorb wird gelesen …";
        var statuses = new List<BinDriveStatus>();
        var entries = new List<BinEntry>();
        var problems = new List<string>();
        int orphans = 0;
        await Task.Run(() =>
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                string root = drive.RootDirectory.FullName;
                try
                {
                    statuses.Add(RecycleBin.Query(root));
                }
                catch (Win32Exception ex)
                {
                    problems.Add($"{root}: {ex.Message}");
                }

                BinListing listing = RecycleBin.List(root);
                entries.AddRange(listing.Entries);
                problems.AddRange(listing.Problems);
                orphans += listing.Orphans;
            }
        });

        if (version != _version)
        {
            return;
        }

        BinRowViewModel[] rows = entries.OrderByDescending(e => e.DeletedUtc).Select(e => new BinRowViewModel(e)).ToArray();
        Drives = statuses;
        Rows = rows;
        SummaryText = TotalItems == 0
            ? "Der Papierkorb ist leer."
            : string.Create(CultureInfo.CurrentCulture, $"{TotalItems:N0} {(TotalItems == 1 ? "Eintrag" : "Einträge")} · {ByteFormat.Format(TotalBytes)} belegt");
        var notes = new List<string>();
        BinDriveStatus[] used = Drives.Where(d => d.Items > 0).ToArray();
        if (used.Length > 1)
        {
            notes.Add("Je Laufwerk: " + string.Join(" · ", used.Select(d => $"{d.DriveRoot.TrimEnd('\\')} {ByteFormat.Format(d.Bytes)}")));
        }

        if (orphans > 0)
        {
            notes.Add(string.Create(CultureInfo.CurrentCulture, $"{orphans:N0} verwaiste Protokolleinträge ohne Inhalt wurden ignoriert; Windows zeigt sie selbst nicht an."));
        }

        if (rows.Length < TotalItems)
        {
            notes.Add(string.Create(CultureInfo.CurrentCulture, $"Die Liste zeigt {rows.Length:N0} von {TotalItems:N0} Einträgen; die übrigen gehören anderen Benutzern oder sind nicht lesbar."));
        }

        if (problems.Count > 0)
        {
            notes.Add(string.Create(CultureInfo.CurrentCulture, $"{problems.Count:N0} Probleme beim Lesen, zum Beispiel: {problems[0]}"));
        }

        NoteText = string.Join(" ", notes.Where(n => n.Length > 0));
        Phase = BinPhase.Idle;

        // Ordnergrößen nachmessen, ohne die Liste warten zu lassen.
        foreach (BinRowViewModel row in rows.Where(r => r.Entry.IsDirectory))
        {
            if (version != _version)
            {
                return;
            }

            try
            {
                (long bytes, int unreadable) = await Task.Run(() => RecycleBin.MeasureDirectory(row.Entry.DataPath, CancellationToken.None));
                if (version == _version)
                {
                    row.SetMeasured(bytes, unreadable);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                row.SetUnmeasurable();
            }
        }
    }

    private async Task RestoreAsync(BinRowViewModel row)
    {
        if (Phase != BinPhase.Idle)
        {
            return;
        }

        Phase = BinPhase.Working;
        RestoreOutcome outcome;
        try
        {
            outcome = await Task.Run(() => RecycleBin.Restore(row.Entry));
        }
        finally
        {
            Phase = BinPhase.Idle;
        }

        switch (outcome.Status)
        {
            case RestoreStatus.Restored:
                ResultText = $"„{row.Name}“ ist wieder in {row.OriginalFolder}." + (outcome.Detail is null ? string.Empty : " " + outcome.Detail);
                Notify?.Invoke(ResultText);
                await _binChanged();
                await RefreshAsync();
                break;
            case RestoreStatus.TargetExists:
                ResultText = $"„{row.Name}“ wurde nicht wiederhergestellt: {outcome.Detail} Nichts wurde überschrieben; benenne die Datei dort um oder verschiebe sie und versuche es erneut.";
                Notify?.Invoke(ResultText);
                break;
            default:
                ResultText = $"„{row.Name}“ ließ sich nicht wiederherstellen: {outcome.Detail}";
                Notify?.Invoke(ResultText);
                break;
        }
    }

    private void RequestEmpty()
    {
        if (Phase != BinPhase.Idle || TotalItems == 0)
        {
            return;
        }

        ConfirmText = string.Create(
            CultureInfo.CurrentCulture,
            $"Papierkorb endgültig leeren? {TotalItems:N0} {(TotalItems == 1 ? "Eintrag" : "Einträge")} ({ByteFormat.Format(TotalBytes)}) werden gelöscht. Das lässt sich nicht rückgängig machen.");
        Phase = BinPhase.ConfirmingEmpty;
    }

    private async Task EmptyAsync()
    {
        if (Phase != BinPhase.ConfirmingEmpty)
        {
            return;
        }

        Phase = BinPhase.Working;
        string[] roots = Drives.Where(d => d.Items > 0).Select(d => d.DriveRoot).ToArray();
        long itemsBefore = TotalItems;
        long bytesBefore = TotalBytes;
        long? freeBefore = FreeSpace(roots);
        string? problem = null;
        try
        {
            await Task.Run(() =>
            {
                foreach (string root in roots)
                {
                    RecycleBin.Empty(root);
                }
            });
        }
        catch (Win32Exception ex)
        {
            problem = ex.Message;
        }
        finally
        {
            Phase = BinPhase.Idle;
        }

        long? freeAfter = FreeSpace(roots);
        CultureInfo culture = CultureInfo.CurrentCulture;
        string measured = freeBefore is long before && freeAfter is long after && after != before
            ? string.Create(culture, $" Am freien Platz gemessen: {ByteFormat.Format(Math.Abs(after - before))} {(after > before ? "mehr" : "weniger")} frei.")
            : string.Empty;
        ResultText = problem is null
            ? string.Create(culture, $"Papierkorb geleert: {itemsBefore:N0} Einträge, laut Papierkorb {ByteFormat.Format(bytesBefore)}.{measured}")
            : $"Der Papierkorb ließ sich nicht (vollständig) leeren: {problem}";
        Notify?.Invoke(ResultText);
        await _binChanged();
        await RefreshAsync();
    }

    private static long? FreeSpace(IEnumerable<string> roots)
    {
        long sum = 0;
        foreach (string root in roots)
        {
            try
            {
                sum += new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        return sum;
    }

    private void OpenInExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = false });
        }
        catch (Win32Exception ex)
        {
            Notify?.Invoke($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }
}
