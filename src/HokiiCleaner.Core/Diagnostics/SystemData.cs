namespace HokiiCleaner.Core.Diagnostics;

/// <summary>Ein Messwert oder der Grund, warum er nicht gemessen werden konnte. Nichts wird stillschweigend ausgelassen.</summary>
public sealed class Measured<T>
{
    private readonly T? _value;

    private Measured(T? value, string? unavailableReason)
    {
        _value = value;
        Reason = unavailableReason;
    }

    public bool IsAvailable => Reason is null;

    /// <summary>Grund, warum nicht gemessen wurde; <c>null</c> bei vorhandenem Wert.</summary>
    public string? Reason { get; }

    public T Value => IsAvailable
        ? _value!
        : throw new InvalidOperationException($"Nicht gemessen: {Reason}");

    public static Measured<T> Of(T value) => new(value, null);

    public static Measured<T> Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new Measured<T>(default, reason);
    }
}

public enum DiskMedia
{
    Unknown,
    Hdd,
    Ssd,
    StorageClassMemory,
}

public enum DiskHealth
{
    Healthy,
    Warning,
    Unhealthy,
    Unknown,
}

public sealed record VolumeInfo(string Name, string? Label, long TotalBytes, long FreeBytes, bool IsSystem)
{
    public double FreePercent => TotalBytes <= 0 ? 0 : 100.0 * FreeBytes / TotalBytes;
}

/// <summary>Eine physische Platte; <paramref name="DriveNames"/> sind die Laufwerke darauf, z. B. <c>C:\</c>.</summary>
public sealed record PhysicalDiskInfo(
    string Id,
    string Name,
    DiskMedia Media,
    string BusType,
    DiskHealth Health,
    long SizeBytes,
    IReadOnlyList<string> DriveNames);

public sealed record DiskActivityInstance(
    string Instance,
    double AverageBusyPercent,
    double MaxBusyPercent,
    double AverageLatencySeconds,
    double MaxQueueLength);

public sealed record DiskActivity(TimeSpan Window, IReadOnlyList<DiskActivityInstance> Instances);

public sealed record StartupEntry(
    string Name,
    string Command,
    string Location,
    bool Enabled,
    string? ExecutablePath,
    bool? ExecutableExists);

public sealed record ScheduledTaskEntry(string Name, string Path, bool Enabled, bool AtLogon, bool AtBoot, string? Command);

/// <summary>Ein Dienst mit Autostart.</summary>
public sealed record ServiceEntry(string Name, string DisplayName, string PathName, bool Running, bool DelayedStart);

public sealed record ProcessSample(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    double IoBytesPerSecond,
    bool CpuMeasured,
    bool IoMeasured);

/// <param name="SystemCpuPercent">Gesamtauslastung aller Kerne über das Messfenster.</param>
public sealed record ProcessSampleResult(TimeSpan Window, double SystemCpuPercent, IReadOnlyList<ProcessSample> Processes);

public sealed record MemoryInfo(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long CommitTotalBytes,
    long CommitLimitBytes,
    long? PageFileAllocatedBytes,
    long? PageFileUsedBytes)
{
    public double AvailablePercent => TotalPhysicalBytes <= 0 ? 0 : 100.0 * AvailablePhysicalBytes / TotalPhysicalBytes;

    public double CommitPercent => CommitLimitBytes <= 0 ? 0 : 100.0 * CommitTotalBytes / CommitLimitBytes;
}

public sealed record SystemConfig(
    string PowerPlanName,
    Guid PowerPlanId,
    TimeSpan Uptime,
    bool? FastStartupEnabled,
    bool WindowsUpdateRebootPending,
    bool ServicingRebootPending,
    bool FileRenamePending);

public sealed record DisplayDriverInfo(string DeviceName, string Manufacturer, DateTime? DriverDate, string? Version);

/// <summary>Eine Erweiterung im Kontextmenü des Explorers (Shell-Erweiterung).</summary>
/// <param name="Scope">Wo sie erscheint, z. B. "Dateien" oder "Ordner".</param>
/// <param name="Library">Die DLL, die geladen wird; <c>null</c>, wenn die Registry keine nennt.</param>
/// <param name="IsWindowsComponent">True, wenn die DLL im Windows-Ordner liegt.</param>
public sealed record ShellExtensionInfo(string Scope, string Name, string Clsid, string? Library, bool IsWindowsComponent);

/// <summary>Eine in einem Browser installierte Erweiterung.</summary>
public sealed record BrowserExtensionInfo(string Browser, string Profile, string Name, string Id);

/// <summary>Platz, den Schattenkopien (Wiederherstellungspunkte) belegen. <paramref name="MaxBytes"/> ist <c>null</c> ohne Limit.</summary>
public sealed record ShadowStorageInfo(long AllocatedBytes, long UsedBytes, long? MaxBytes);

/// <summary>Ein Gerätetreiber laut Windows.</summary>
public sealed record DriverInfo(string DeviceName, string Manufacturer, string DeviceClass, string? Version, DateTime? Date);

/// <summary>Alle Rohmessungen eines Durchlaufs. Jede Messung kann einzeln nicht verfügbar sein.</summary>
public sealed record SlownessSnapshot(
    bool IsElevated,
    Measured<IReadOnlyList<VolumeInfo>> Volumes,
    Measured<IReadOnlyList<PhysicalDiskInfo>> Disks,
    Measured<DiskActivity> DiskActivity,
    Measured<IReadOnlyList<StartupEntry>> StartupEntries,
    Measured<IReadOnlyList<ScheduledTaskEntry>> Tasks,
    Measured<IReadOnlyList<ServiceEntry>> Services,
    Measured<ProcessSampleResult> Processes,
    Measured<MemoryInfo> Memory,
    Measured<SystemConfig> Config,
    Measured<IReadOnlyList<DisplayDriverInfo>> DisplayDrivers,
    Measured<IReadOnlyList<ShellExtensionInfo>> ShellExtensions,
    Measured<IReadOnlyList<BrowserExtensionInfo>> BrowserExtensions,
    Measured<IReadOnlyList<DriverInfo>> Drivers,
    Measured<ShadowStorageInfo> ShadowStorage);
