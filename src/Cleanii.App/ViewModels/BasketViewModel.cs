using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Cleanii.Actions;
using Cleanii.App.Infrastructure;
using Cleanii.Core;

namespace Cleanii.App.ViewModels;

/// <summary>Ein Eintrag, den der Benutzer für den Papierkorb markiert hat; Größen stammen aus dem Scan.</summary>
/// <param name="Source">Wo markiert wurde (Orte, Typen, Duplikate, Entwickler); nur zur Anzeige.</param>
public sealed record BasketEntry(string Path, bool IsDirectory, long SizeOnDisk, long FileCount, string Source);

public sealed class BasketItemViewModel
{
    public BasketItemViewModel(BasketEntry entry)
    {
        Entry = entry;
        Path = entry.Path;
        Name = System.IO.Path.GetFileName(entry.Path.TrimEnd('\\')) is { Length: > 0 } name ? name : entry.Path;
        ParentPath = System.IO.Path.GetDirectoryName(entry.Path.TrimEnd('\\')) ?? string.Empty;
        SizeText = ByteFormat.Format(entry.SizeOnDisk);
        DetailText = entry.IsDirectory
            ? string.Create(CultureInfo.CurrentCulture, $"Ordner · {entry.FileCount:N0} {(entry.FileCount == 1 ? "Datei" : "Dateien")} · {entry.Source}")
            : $"Datei · {entry.Source}";
    }

    public BasketEntry Entry { get; }

    public string Path { get; }

    public string Name { get; }

    public string ParentPath { get; }

    public string SizeText { get; }

    public string DetailText { get; }

    public bool IsDirectory => Entry.IsDirectory;

    public long SizeBytes => Entry.SizeOnDisk;

    public long FileCount => Entry.FileCount;
}

public enum BasketPhase
{
    Collecting,
    Planning,
    Confirming,
    Moving,
}

/// <summary>
/// Der Auswahlkorb: sammelt Dateien und Ordner, die der Benutzer in den Papierkorb legen möchte, quer durch alle Ansichten.
/// Nichts wird automatisch markiert oder verschoben. Verschoben wird erst nach einer Vorschau mit ausdrücklicher Bestätigung,
/// über den Papierkorb (wiederherstellbar) und nie etwas aus der Schutzliste.
/// </summary>
public sealed class BasketViewModel : ObservableObject
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cleanii", "logs");

    private PathGuard _guard;
    private readonly IBinPolicy _policy;
    private readonly Func<IReadOnlyList<RecycleCandidate>, Task> _afterMoved;
    private readonly Func<IReadOnlyList<IReadOnlyList<string>>>? _duplicateGroups;
    private readonly Action? _showBin;
    private readonly Dictionary<string, BasketItemViewModel> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folderKeys = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<BasketItemViewModel> _items = Array.Empty<BasketItemViewModel>();
    private BasketPhase _phase = BasketPhase.Collecting;
    private bool _isListOpen;
    private RecyclePlan? _plan;
    private CancellationTokenSource? _cts;
    private string _confirmTitle = string.Empty;
    private IReadOnlyList<string> _confirmLines = Array.Empty<string>();
    private string _blockedText = string.Empty;
    private string _progressText = string.Empty;
    private double _progressFraction;
    private string _resultTitle = string.Empty;
    private string _resultNote = string.Empty;
    private IReadOnlyList<string> _resultLines = Array.Empty<string>();
    private string? _logPath;

    public BasketViewModel(
        Func<IReadOnlyList<RecycleCandidate>, Task> afterMoved,
        Func<IReadOnlyList<IReadOnlyList<string>>>? duplicateGroups = null,
        Action? showBin = null,
        PathGuard? guard = null,
        IBinPolicy? policy = null)
    {
        _afterMoved = afterMoved;
        _duplicateGroups = duplicateGroups;
        _showBin = showBin;
        _guard = guard ?? PathGuard.ForCurrentUser();
        _policy = policy ?? new SystemBinPolicy();

        ToggleListCommand = new RelayCommand(() => IsListOpen = !IsListOpen, () => HasItems);
        ClearCommand = new RelayCommand(Clear, () => HasItems && Phase == BasketPhase.Collecting);
        RemoveItemCommand = new RelayCommand<BasketItemViewModel>(item => Remove(item.Path), _ => Phase == BasketPhase.Collecting);
        RequestMoveCommand = new RelayCommand(() => _ = RequestMoveAsync(), () => HasItems && Phase == BasketPhase.Collecting);
        ConfirmCommand = new RelayCommand(() => _ = MoveAsync(), () => Phase == BasketPhase.Confirming && _plan is { Accepted.Count: > 0 });
        CancelConfirmCommand = new RelayCommand(() => Phase = BasketPhase.Collecting, () => Phase == BasketPhase.Confirming);
        CancelMoveCommand = new RelayCommand(() => _cts?.Cancel(), () => Phase == BasketPhase.Moving);
        DismissResultCommand = new RelayCommand(ClearResult, () => HasResult && Phase == BasketPhase.Collecting);
        DismissBlockedCommand = new RelayCommand(() => BlockedText = string.Empty, () => HasBlocked);
        ShowBinCommand = new RelayCommand(() => _showBin?.Invoke(), () => _showBin is not null);
        RevealLogCommand = new RelayCommand(RevealLog, () => LogPath is not null);
    }

    /// <summary>Meldung für die Statuszeile (z. B. warum etwas nicht markiert werden konnte).</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>Wird bei jeder Änderung der Auswahl ausgelöst; Ansichten frischen damit ihre Haken auf.</summary>
    public event EventHandler? Changed;

    public ICommand ToggleListCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand RemoveItemCommand { get; }

    public ICommand RequestMoveCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelConfirmCommand { get; }

    public ICommand CancelMoveCommand { get; }

    public ICommand DismissResultCommand { get; }

    public ICommand DismissBlockedCommand { get; }

    public ICommand ShowBinCommand { get; }

    public ICommand RevealLogCommand { get; }

    public IReadOnlyList<BasketItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (Set(ref _items, value))
            {
                Raise(nameof(HasItems));
                Raise(nameof(Count));
                Raise(nameof(TotalBytes));
                Raise(nameof(TotalFiles));
                Raise(nameof(SummaryText));
                Raise(nameof(IsVisible));
                Raise(nameof(ShowCollectBar));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasItems => Items.Count > 0;

    public int Count => Items.Count;

    public long TotalBytes => Items.Sum(i => i.SizeBytes);

    public long TotalFiles => Items.Sum(i => i.FileCount);

    public string SummaryText => Items.Count == 0
        ? string.Empty
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{Items.Count:N0} {(Items.Count == 1 ? "Eintrag" : "Einträge")} · {ByteFormat.Format(TotalBytes)} ausgewählt");

    public BasketPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value))
            {
                return;
            }

            Raise(nameof(IsCollecting));
            Raise(nameof(IsPlanning));
            Raise(nameof(IsConfirming));
            Raise(nameof(IsMoving));
            Raise(nameof(IsVisible));
            Raise(nameof(ShowCollectBar));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsCollecting => Phase == BasketPhase.Collecting;

    public bool IsPlanning => Phase == BasketPhase.Planning;

    public bool IsConfirming => Phase == BasketPhase.Confirming;

    public bool IsMoving => Phase == BasketPhase.Moving;

    /// <summary>Die Leiste zeigt die Auswahl (statt Bestätigung oder Fortschritt) und hat etwas auszuwählen.</summary>
    public bool ShowCollectBar => Phase is BasketPhase.Collecting or BasketPhase.Planning && HasItems;

    public bool IsVisible => HasItems || HasResult || HasBlocked || Phase != BasketPhase.Collecting;

    public bool IsListOpen
    {
        get => _isListOpen;
        set => Set(ref _isListOpen, value);
    }

    public string ConfirmTitle
    {
        get => _confirmTitle;
        private set => Set(ref _confirmTitle, value);
    }

    public IReadOnlyList<string> ConfirmLines
    {
        get => _confirmLines;
        private set => Set(ref _confirmLines, value);
    }

    /// <summary>Warum die Bestätigung nicht angeboten wird (z. B. alles geschützt oder eine Duplikat-Gruppe wäre leer).</summary>
    public string BlockedText
    {
        get => _blockedText;
        private set
        {
            if (Set(ref _blockedText, value))
            {
                Raise(nameof(HasBlocked));
                Raise(nameof(IsVisible));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasBlocked => BlockedText.Length > 0;

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

    public bool HasResult => ResultTitle.Length > 0;

    public string ResultTitle
    {
        get => _resultTitle;
        private set
        {
            if (Set(ref _resultTitle, value))
            {
                Raise(nameof(HasResult));
                Raise(nameof(IsVisible));
                CommandManager.InvalidateRequerySuggested();
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
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasLog => LogPath is not null;

    // ------------------------------------------------------------ Auswahl

    /// <summary>
    /// Ersetzt die Schutzliste (z. B. nachdem der Benutzer einen Ordner geschützt hat). Was dadurch geschützt ist, verlässt die
    /// Auswahl und wird gemeldet.
    /// </summary>
    public void UseGuard(PathGuard guard)
    {
        _guard = guard;
        string[] blocked = _byKey.Values.Where(i => !guard.Check(i.Path, probeFileSystem: false).Allowed).Select(i => i.Path).ToArray();
        if (blocked.Length > 0)
        {
            RemoveRange(blocked);
            Notify?.Invoke($"{blocked.Length:N0} Einträge sind jetzt geschützt und wurden aus der Auswahl genommen.");
        }
    }

    public bool Contains(string path) => _byKey.ContainsKey(Key(path));

    /// <summary>Prüft nur nach Namen und Lage (ohne Dateisystem), ob ein Pfad überhaupt markiert werden darf.</summary>
    public bool CanMark(string path) => _guard.Check(path, probeFileSystem: false).Allowed;

    /// <summary>Warum ein Pfad nicht markierbar ist; <c>null</c>, wenn er es ist.</summary>
    public string? WhyNotMarkable(string path)
    {
        GuardVerdict verdict = _guard.Check(path, probeFileSystem: false);
        return verdict.Allowed ? null : verdict.Reason;
    }

    /// <summary>Markiert einen Eintrag. Gibt <c>false</c> zurück (mit Meldung), wenn er nicht aufgenommen wurde.</summary>
    public bool Add(BasketEntry entry)
    {
        AddResult result = AddCore(entry);
        if (result.Message is not null)
        {
            Notify?.Invoke(result.Message);
        }

        if (result.Changed)
        {
            Publish();
        }

        return result.InBasket;
    }

    /// <summary>Markiert viele Einträge auf einmal und meldet nur eine Zusammenfassung. Gibt die Zahl der neu aufgenommenen zurück.</summary>
    public int AddRange(IEnumerable<BasketEntry> entries)
    {
        int added = 0;
        int skipped = 0;
        bool changed = false;
        string? firstReason = null;
        foreach (BasketEntry entry in entries)
        {
            bool wasThere = Contains(entry.Path);
            AddResult result = AddCore(entry);
            changed |= result.Changed;
            if (result.InBasket && !wasThere)
            {
                added++;
            }
            else if (!result.InBasket)
            {
                skipped++;
                firstReason ??= result.Message;
            }
        }

        if (changed)
        {
            Publish();
        }

        if (skipped > 0)
        {
            Notify?.Invoke(string.Create(CultureInfo.CurrentCulture, $"{skipped:N0} Einträge nicht markiert. Zum Beispiel: {firstReason}"));
        }

        return added;
    }

    public bool Remove(string path)
    {
        if (Phase is BasketPhase.Moving or BasketPhase.Planning)
        {
            return false;
        }

        if (!_byKey.Remove(Key(path), out BasketItemViewModel? item))
        {
            return false;
        }

        _folderKeys.Remove(Key(path));
        Rebuild();
        if (Phase == BasketPhase.Confirming)
        {
            _plan = null;
            Phase = BasketPhase.Collecting; // die Vorschau gilt nicht mehr
        }

        Publish();
        return item is not null;
    }

    public void RemoveRange(IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (string path in paths)
        {
            string key = Key(path);
            if (_byKey.Remove(key))
            {
                _folderKeys.Remove(key);
                changed = true;
            }
        }

        if (changed)
        {
            Rebuild();
            Publish();
        }
    }

    public void Clear()
    {
        if (Phase is BasketPhase.Moving or BasketPhase.Planning)
        {
            return;
        }

        bool had = _byKey.Count > 0;
        _byKey.Clear();
        _folderKeys.Clear();
        _plan = null;
        BlockedText = string.Empty;
        ClearResult();
        if (Phase == BasketPhase.Confirming)
        {
            Phase = BasketPhase.Collecting;
        }

        if (had)
        {
            Rebuild();
            Publish();
        }
    }

    /// <summary>Wahr, wenn der Pfad selbst oder ein Ordner darüber markiert ist.</summary>
    public bool Covers(string path)
    {
        for (string? current = Key(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (_byKey.ContainsKey(current))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct AddResult(bool InBasket, bool Changed, string? Message);

    private AddResult AddCore(BasketEntry entry)
    {
        if (Phase is BasketPhase.Moving or BasketPhase.Planning)
        {
            return new AddResult(false, false, "Gerade läuft ein Vorgang; die Auswahl lässt sich danach ändern.");
        }

        string key = Key(entry.Path);
        if (_byKey.ContainsKey(key))
        {
            return new AddResult(true, false, null);
        }

        for (string? parent = Path.GetDirectoryName(key); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (_folderKeys.Contains(parent))
            {
                return new AddResult(false, false, $"„{Path.GetFileName(key)}“ liegt schon im gewählten Ordner „{Path.GetFileName(parent)}“.");
            }
        }

        string? why = WhyNotMarkable(entry.Path);
        if (why is not null)
        {
            return new AddResult(false, false, $"„{Path.GetFileName(key)}“ lässt sich nicht verschieben: {why}");
        }

        if (entry.IsDirectory)
        {
            // Was schon darin markiert war, zählt jetzt mit dem Ordner.
            string prefix = key + Path.DirectorySeparatorChar;
            foreach (string inner in _byKey.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _byKey.Remove(inner);
                _folderKeys.Remove(inner);
            }

            _folderKeys.Add(key);
        }

        _byKey[key] = new BasketItemViewModel(entry with { Path = Normalize(entry.Path) });
        return new AddResult(true, true, null);
    }

    private void Rebuild()
        => Items = _byKey.Values.OrderByDescending(i => i.SizeBytes).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    private void Publish()
    {
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Key(string path) => Normalize(path);

    // ------------------------------------------------------------ Verschieben

    private async Task RequestMoveAsync()
    {
        if (Phase != BasketPhase.Collecting || Items.Count == 0)
        {
            return;
        }

        ClearResult();
        BlockedText = string.Empty;
        Phase = BasketPhase.Planning;
        RecycleCandidate[] candidates = Items
            .Select(i => new RecycleCandidate(i.Path, i.IsDirectory, i.SizeBytes, i.FileCount))
            .ToArray();

        RecyclePlan plan;
        try
        {
            plan = await Task.Run(() => RecycleEngine.Plan(candidates, _guard, _policy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Phase = BasketPhase.Collecting;
            BlockedText = $"Die Auswahl konnte nicht geprüft werden: {ex.Message}";
            return;
        }

        string? emptiedGroup = FindEmptiedDuplicateGroup(plan);
        if (emptiedGroup is not null)
        {
            Phase = BasketPhase.Collecting;
            BlockedText = $"In einer Gruppe identischer Dateien würden alle Kopien verschoben (zum Beispiel {emptiedGroup}). Mindestens eine Kopie muss bleiben. Nimm eine aus der Auswahl.";
            return;
        }

        if (plan.Accepted.Count == 0)
        {
            Phase = BasketPhase.Collecting;
            BlockedText = "Nichts davon lässt sich verschieben. " + DescribeRefusals(plan, 5);
            return;
        }

        _plan = plan;
        CultureInfo culture = CultureInfo.CurrentCulture;
        ConfirmTitle = string.Create(
            culture,
            $"{plan.Accepted.Count:N0} {(plan.Accepted.Count == 1 ? "Eintrag" : "Einträge")} ({ByteFormat.Format(plan.AcceptedBytes)}) in den Papierkorb verschieben?");

        var lines = new List<string>
        {
            "Du kannst sie dort wiederherstellen. Der Platz wird erst frei, wenn der Papierkorb geleert ist.",
        };
        lines.AddRange(plan.Notes);
        if (plan.Refused.Count > 0)
        {
            lines.Add(string.Create(culture, $"{plan.Refused.Count:N0} {(plan.Refused.Count == 1 ? "Eintrag wird" : "Einträge werden")} nicht verschoben:"));
            lines.AddRange(plan.Refused.Take(5).Select(r => $"   {Path.GetFileName(r.Item.Path)} – {r.Reason}"));
            if (plan.Refused.Count > 5)
            {
                lines.Add(string.Create(culture, $"   … und {plan.Refused.Count - 5:N0} weitere"));
            }
        }

        ConfirmLines = lines;
        Phase = BasketPhase.Confirming;
    }

    private static string DescribeRefusals(RecyclePlan plan, int max)
    {
        string[] parts = plan.Refused.Take(max).Select(r => $"{Path.GetFileName(r.Item.Path)}: {r.Reason}").ToArray();
        return string.Join(" ", parts) + (plan.Refused.Count > max ? $" … und {plan.Refused.Count - max} weitere." : string.Empty);
    }

    /// <summary>Prüft, ob die Auswahl alle Kopien einer Gruppe identischer Dateien verschieben würde; liefert eine Datei daraus oder <c>null</c>.</summary>
    private string? FindEmptiedDuplicateGroup(RecyclePlan plan)
    {
        if (_duplicateGroups is null)
        {
            return null;
        }

        var selected = new HashSet<string>(plan.Accepted.Select(a => Key(a.Path)), StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(plan.Accepted.Where(a => a.IsDirectory).Select(a => Key(a.Path)), StringComparer.OrdinalIgnoreCase);

        bool IsMoved(string path)
        {
            string key = Key(path);
            if (selected.Contains(key))
            {
                return true;
            }

            for (string? parent = Path.GetDirectoryName(key); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (folders.Contains(parent))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (IReadOnlyList<string> group in _duplicateGroups())
        {
            if (group.Count > 0 && group.All(IsMoved))
            {
                return Path.GetFileName(group[0]);
            }
        }

        return null;
    }

    private async Task MoveAsync()
    {
        if (Phase != BasketPhase.Confirming || _plan is not { Accepted.Count: > 0 } plan)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        ProgressFraction = 0;
        ProgressText = "Starte …";
        Phase = BasketPhase.Moving;

        var progress = new Progress<RecycleProgress>(p =>
        {
            ProgressFraction = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = string.Create(CultureInfo.CurrentCulture, $"{p.Done:N0} von {p.Total:N0} verschoben · {Path.GetFileName(p.CurrentPath)}");
        });

        RecycleResult result;
        string? unexpected = null;
        try
        {
            result = await Task.Run(() => RecycleEngine.Run(plan, _guard, new ShellRecycler(), progress, token, RecycleBin.CountItems));
        }
        catch (Exception ex)
        {
            // Unerwartet: nicht raten, was verschoben wurde, sondern nachsehen.
            unexpected = ex.Message;
            result = new RecycleResult(
                plan.Accepted
                    .Select(c => File.Exists(c.Path) || Directory.Exists(c.Path)
                        ? new RecycleItemResult(c, RecycleStatus.NotAttempted, null)
                        : new RecycleItemResult(c, RecycleStatus.Moved, null))
                    .ToArray(),
                DateTime.UtcNow, DateTime.UtcNow, Cancelled: false, BinItemsBefore: null, BinItemsAfter: null);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        await FinishAsync(plan, result, unexpected);
    }

    private async Task FinishAsync(RecyclePlan plan, RecycleResult result, string? unexpected)
    {
        RecycleCandidate[] moved = result.Moved.Select(m => m.Item).ToArray();

        // Verschobene und abgelehnte Einträge verlassen die Auswahl; gescheiterte bleiben für einen neuen Versuch.
        _plan = null;
        RemoveRange(moved.Select(m => m.Path).Concat(plan.Refused.Select(r => r.Item.Path)));
        Phase = BasketPhase.Collecting;

        string? logProblem = null;
        try
        {
            LogPath = RecycleLog.Write(LogDirectory, result, plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logProblem = ex.Message;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        ResultTitle = moved.Length == 0
            ? "Nichts wurde verschoben"
            : string.Create(culture, $"{moved.Length:N0} {(moved.Length == 1 ? "Eintrag" : "Einträge")} ({ByteFormat.Format(result.MovedBytes)}) im Papierkorb");
        var note = new List<string>();
        if (moved.Length > 0)
        {
            note.Add("Der Platz wird erst frei, wenn der Papierkorb geleert ist. Bis dahin lässt sich alles wiederherstellen.");
        }

        if (result.Cancelled)
        {
            note.Add("Der Vorgang wurde abgebrochen; was schon im Papierkorb liegt, bleibt dort.");
        }

        if (result.BinGrowthLooksShort)
        {
            note.Add("Achtung: Der Papierkorb zählt weniger neue Einträge, als verschoben wurden. Windows hat möglicherweise einzelne Einträge endgültig gelöscht. Prüfe die Papierkorb-Ansicht.");
        }

        if (unexpected is not null)
        {
            note.Add($"Unerwarteter Fehler: {unexpected}. Der Zustand wurde am Dateisystem nachgeprüft.");
        }

        if (logProblem is not null)
        {
            note.Add($"Das Protokoll konnte nicht geschrieben werden: {logProblem}");
        }

        ResultNote = string.Join(" ", note);

        var lines = new List<string>();
        foreach (RecycleItemResult problem in result.Items.Where(i => i.Status is RecycleStatus.Failed or RecycleStatus.Refused).Take(6))
        {
            lines.Add($"{Path.GetFileName(problem.Item.Path)}: {problem.Detail ?? "nicht verschoben"}");
        }

        int more = result.Items.Count(i => i.Status is RecycleStatus.Failed or RecycleStatus.Refused) - lines.Count;
        if (more > 0)
        {
            lines.Add(string.Create(culture, $"… und {more:N0} weitere"));
        }

        if (plan.Refused.Count > 0)
        {
            lines.Add(string.Create(culture, $"{plan.Refused.Count:N0} Einträge waren geschützt oder nicht verschiebbar und wurden nicht angefasst."));
        }

        ResultLines = lines;

        if (moved.Length > 0)
        {
            try
            {
                await _afterMoved(moved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Notify?.Invoke($"Die Ansicht konnte nicht aktualisiert werden: {ex.Message}. Ein neuer Scan zeigt den aktuellen Stand.");
            }
        }
    }

    private void ClearResult()
    {
        ResultTitle = string.Empty;
        ResultNote = string.Empty;
        ResultLines = Array.Empty<string>();
        LogPath = null;
    }

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
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Notify?.Invoke($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }
}
