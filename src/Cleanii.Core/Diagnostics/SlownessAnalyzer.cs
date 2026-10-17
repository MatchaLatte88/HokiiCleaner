using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Security;
using System.Security.Principal;

namespace Cleanii.Core.Diagnostics;

public sealed record SlownessOptions
{
    /// <summary>Dauer der Stichprobe für Prozessor, Prozesse und Plattenauslastung.</summary>
    public TimeSpan SampleWindow { get; init; } = TimeSpan.FromSeconds(5);

    public SlownessThresholds Thresholds { get; init; } = SlownessThresholds.Default;
}

public sealed record SlownessReport(SlownessSnapshot Snapshot, IReadOnlyList<Check> Checks, TimeSpan Elapsed);

/// <summary>
/// Misst den Zustand des Rechners und wertet ihn aus. Rein lesend: Es wird nichts verändert, deaktiviert oder gelöscht.
/// Eine einzelne Messung, die scheitert (z. B. mangels Administratorrechten), erscheint als "nicht gemessen" mit Grund.
/// </summary>
public static class SlownessAnalyzer
{
    public static async Task<SlownessReport> RunAsync(
        SlownessOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SlownessOptions();
        var stopwatch = Stopwatch.StartNew();

        progress?.Report($"Messe {options.SampleWindow.TotalSeconds:N0} Sekunden lang Prozessor, Prozesse und Platte …");
        Task<Measured<ProcessSampleResult>> processes = MeasureAsync(() => ProcessProbe.SampleAsync(options.SampleWindow, cancellationToken));
        Task<Measured<DiskActivity>> activity = MeasureAsync(() => StorageProbe.SampleActivityAsync(options.SampleWindow, cancellationToken));

        // Diese Abfragen dauern einige Sekunden und laufen deshalb neben der Stichprobe.
        Task<Measured<IReadOnlyList<DriverInfo>>> allDrivers = MeasureAsync(() => Task.Run(DriverProbe.ReadAll, cancellationToken));
        Task<Measured<IReadOnlyList<BrowserExtensionInfo>>> browserExtensions =
            MeasureAsync(() => Task.Run(BrowserExtensionProbe.Read, cancellationToken));
        Task<Measured<ShadowStorageInfo>> shadowStorage = MeasureAsync(() => Task.Run(ShadowStorageProbe.Read, cancellationToken));

        var volumes = Measure(StorageProbe.ReadVolumes);
        var disks = Measure(StorageProbe.ReadPhysicalDisks);
        var startup = Measure(StartupProbe.ReadEntries);
        var tasks = Measure(StartupProbe.ReadTasks);
        var services = Measure(StartupProbe.ReadAutoStartServices);
        var memory = Measure(SystemProbe.ReadMemory);
        var config = Measure(SystemProbe.ReadConfig);
        progress?.Report("Lese Grafiktreiber …");
        var drivers = Measure(SystemProbe.ReadDisplayDrivers);
        var shellExtensions = Measure(ShellExtensionProbe.Read);

        await Task.WhenAll(processes, activity, allDrivers, browserExtensions, shadowStorage).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new SlownessSnapshot(
            IsElevated(),
            volumes,
            disks,
            await activity.ConfigureAwait(false),
            startup,
            tasks,
            services,
            await processes.ConfigureAwait(false),
            memory,
            config,
            drivers,
            shellExtensions,
            await browserExtensions.ConfigureAwait(false),
            await allDrivers.ConfigureAwait(false),
            await shadowStorage.ConfigureAwait(false));

        IReadOnlyList<Check> checks = SlownessRules.Evaluate(snapshot, options.Thresholds);
        return new SlownessReport(snapshot, checks, stopwatch.Elapsed);
    }

    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Measured<T> Measure<T>(Func<T> probe)
    {
        try
        {
            return Measured<T>.Of(probe());
        }
        catch (Exception ex) when (IsExpectedProbeFailure(ex))
        {
            return Measured<T>.Unavailable(Describe(ex));
        }
    }

    private static async Task<Measured<T>> MeasureAsync<T>(Func<Task<T>> probe)
    {
        try
        {
            return Measured<T>.Of(await probe().ConfigureAwait(false));
        }
        catch (Exception ex) when (IsExpectedProbeFailure(ex))
        {
            return Measured<T>.Unavailable(Describe(ex));
        }
    }

    /// <summary>
    /// Nur Fehler, die zum Betrieb gehören (Rechte, WMI, Dateisystem, fehlende Leistungsindikatoren), werden zu "nicht gemessen".
    /// Alles andere, auch ein Abbruch, ist ein Programmfehler bzw. gewollt und läuft weiter nach oben.
    /// </summary>
    private static bool IsExpectedProbeFailure(Exception ex)
        => ex is ManagementException or UnauthorizedAccessException or Win32Exception or IOException
            or InvalidOperationException or SecurityException;

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Zugriff verweigert; vermutlich sind Administratorrechte nötig. " + ex.Message,
        ManagementException management when management.ErrorCode == ManagementStatus.AccessDenied
            => "Zugriff verweigert; vermutlich sind Administratorrechte nötig. " + ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
