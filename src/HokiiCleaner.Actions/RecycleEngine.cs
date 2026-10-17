using System.Globalization;

namespace HokiiCleaner.Actions;

/// <summary>
/// Plant und führt das Verschieben ausgewählter Einträge in den Papierkorb aus. Das ist wiederherstellbar, aber trotzdem
/// ein Eingriff: Es gibt nur, was der Benutzer ausdrücklich gewählt hat, nie etwas Automatisches, und nichts aus der Schutzliste.
/// </summary>
public static class RecycleEngine
{
    /// <summary>
    /// Prüft die Auswahl, ohne etwas zu verändern: Schutzliste, Verknüpfungen, Papierkorb des Laufwerks und dessen Größengrenze.
    /// Einträge, die in einem ebenfalls gewählten Ordner liegen, werden nicht doppelt aufgeführt.
    /// </summary>
    public static RecyclePlan Plan(IEnumerable<RecycleCandidate> candidates, PathGuard guard, IBinPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(policy);

        RecycleCandidate[] items = candidates
            .Select(c => c with { Path = Path.GetFullPath(c.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) })
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Path.Length)
            .ToArray();

        var accepted = new List<RecycleCandidate>();
        var refused = new List<RecycleRefusal>();
        var notes = new List<string>();
        var driveCache = new Dictionary<string, BinDriveInfo>(StringComparer.OrdinalIgnoreCase);
        int nested = 0;

        foreach (RecycleCandidate item in items)
        {
            if (accepted.Any(a => a.IsDirectory && item.Path.StartsWith(a.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                nested++; // steckt schon in einem gewählten Ordner
                continue;
            }

            GuardVerdict verdict = guard.Check(item.Path, probeFileSystem: true);
            if (!verdict.Allowed)
            {
                refused.Add(new RecycleRefusal(item, verdict.Reason!));
                continue;
            }

            // Je Laufwerk einmal abfragen: Registry und Laufwerksinfo sind für Tausende Einträge sonst unnötig teuer.
            string drive = Path.GetPathRoot(item.Path) ?? string.Empty;
            if (!driveCache.TryGetValue(drive, out BinDriveInfo? bin))
            {
                bin = policy.Describe(item.Path);
                driveCache[drive] = bin;
            }

            if (bin.WhyNot is not null)
            {
                refused.Add(new RecycleRefusal(item, bin.WhyNot));
                continue;
            }

            if (bin.MaxBytes is long max && item.SizeOnDisk >= max)
            {
                refused.Add(new RecycleRefusal(
                    item,
                    string.Create(CultureInfo.CurrentCulture, $"Größer als der Papierkorb des Laufwerks (höchstens {Format(max)}); Windows würde ihn endgültig löschen.")));
                continue;
            }

            accepted.Add(item);
        }

        if (nested > 0)
        {
            notes.Add(string.Create(CultureInfo.CurrentCulture, $"{nested:N0} {(nested == 1 ? "Eintrag liegt" : "Einträge liegen")} in einem ebenfalls gewählten Ordner und {(nested == 1 ? "wird" : "werden")} mit ihm verschoben."));
        }

        foreach (string provider in accepted.Select(a => PathGuard.SyncProvider(a.Path)).OfType<string>().Distinct())
        {
            int count = accepted.Count(a => PathGuard.SyncProvider(a.Path) == provider);
            notes.Add(string.Create(
                CultureInfo.CurrentCulture,
                $"{count:N0} {(count == 1 ? "Eintrag liegt" : "Einträge liegen")} in {provider}. Das Löschen wird mit der Cloud abgeglichen; {provider} hat dort einen eigenen Papierkorb."));
        }

        return new RecyclePlan(accepted, refused, notes);
    }

    /// <summary>
    /// Verschiebt die geplanten Einträge nacheinander in den Papierkorb. Jeder Eintrag wird unmittelbar davor noch einmal
    /// geprüft; Fehler bei einem Eintrag stoppen die übrigen nicht, werden aber gemeldet.
    /// </summary>
    public static RecycleResult Run(
        RecyclePlan plan,
        PathGuard guard,
        IRecycler recycler,
        IProgress<RecycleProgress>? progress,
        CancellationToken ct,
        Func<IReadOnlyList<string>, long?>? binItemCount = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(recycler);

        DateTime started = DateTime.UtcNow;
        string[] drives = plan.Accepted
            .Select(a => Path.GetPathRoot(a.Path) ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long? before = binItemCount?.Invoke(drives);

        var results = new List<RecycleItemResult>();
        bool cancelled = false;
        for (int i = 0; i < plan.Accepted.Count; i++)
        {
            RecycleCandidate item = plan.Accepted[i];
            if (cancelled || ct.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new RecycleItemResult(item, RecycleStatus.NotAttempted, null));
                continue;
            }

            progress?.Report(new RecycleProgress(i, plan.Accepted.Count, item.Path));
            results.Add(MoveOne(item, guard, recycler));
        }

        progress?.Report(new RecycleProgress(plan.Accepted.Count, plan.Accepted.Count, string.Empty));
        long? after = binItemCount?.Invoke(drives);
        return new RecycleResult(results, started, DateTime.UtcNow, cancelled, before, after);
    }

    private static RecycleItemResult MoveOne(RecycleCandidate item, PathGuard guard, IRecycler recycler)
    {
        GuardVerdict verdict = guard.Check(item.Path, probeFileSystem: true);
        if (!verdict.Allowed)
        {
            return new RecycleItemResult(item, RecycleStatus.Refused, verdict.Reason);
        }

        RecycleOutcome outcome;
        try
        {
            outcome = recycler.Recycle(item.Path, item.IsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            return new RecycleItemResult(item, RecycleStatus.Failed, ex.Message);
        }

        if (!outcome.Success)
        {
            return new RecycleItemResult(item, RecycleStatus.Failed, outcome.Detail);
        }

        // Windows meldet Erfolg auch dann, wenn nichts passiert ist (z. B. wenn der Benutzer einen Dialog abbricht): nachsehen.
        return File.Exists(item.Path) || Directory.Exists(item.Path)
            ? new RecycleItemResult(item, RecycleStatus.Failed, "Windows meldete Erfolg, der Eintrag ist aber noch da.")
            : new RecycleItemResult(item, RecycleStatus.Moved, null);
    }

    private static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }
}
