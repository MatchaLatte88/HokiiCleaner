using System.Management;
using System.Diagnostics;

namespace Cleanii.Core.Diagnostics;

/// <summary>Liest Laufwerke, Platten und deren Auslastung. Rein lesend.</summary>
internal static class StorageProbe
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<VolumeInfo> ReadVolumes()
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory)
            ?? throw new InvalidOperationException("Systemlaufwerk nicht ermittelbar.");

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new VolumeInfo(
                d.RootDirectory.FullName,
                string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel,
                d.TotalSize,
                d.AvailableFreeSpace,
                string.Equals(d.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static IReadOnlyList<PhysicalDiskInfo> ReadPhysicalDisks()
    {
        // Laufwerksbuchstaben je Plattennummer (MSFT_Partition.DiskNumber entspricht MSFT_PhysicalDisk.DeviceId).
        var drivesByDisk = new Dictionary<string, List<string>>();
        using (var partitions = new ManagementObjectSearcher(StorageScope, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
        {
            foreach (ManagementBaseObject partition in partitions.Get())
            {
                using (partition)
                {
                    char letter = partition["DriveLetter"] is null ? '\0' : Convert.ToChar(partition["DriveLetter"]);
                    if (letter == '\0' || partition["DiskNumber"] is null)
                    {
                        continue;
                    }

                    string disk = Convert.ToUInt32(partition["DiskNumber"]).ToString();
                    if (!drivesByDisk.TryGetValue(disk, out List<string>? list))
                    {
                        drivesByDisk[disk] = list = new List<string>();
                    }

                    list.Add($"{char.ToUpperInvariant(letter)}:\\");
                }
            }
        }

        var result = new List<PhysicalDiskInfo>();
        using var disks = new ManagementObjectSearcher(
            StorageScope, "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk");
        foreach (ManagementBaseObject disk in disks.Get())
        {
            using (disk)
            {
                string id = Convert.ToString(disk["DeviceId"]) ?? throw new InvalidOperationException("Platte ohne DeviceId.");
                drivesByDisk.TryGetValue(id, out List<string>? letters);
                result.Add(new PhysicalDiskInfo(
                    id,
                    Convert.ToString(disk["FriendlyName"]) ?? $"Platte {id}",
                    MapMedia(disk["MediaType"]),
                    MapBus(disk["BusType"]),
                    MapHealth(disk["HealthStatus"]),
                    disk["Size"] is null ? 0 : (long)Convert.ToUInt64(disk["Size"]),
                    (IReadOnlyList<string>?)letters?.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()));
            }
        }

        return result.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Misst die Plattenauslastung über das Fenster in Sekundenschritten. Der erste Wert eines Leistungsindikators ist immer 0
    /// und dient nur zum Vorbereiten; ausgewertet werden alle folgenden Abfragen.
    /// </summary>
    public static async Task<DiskActivity> SampleActivityAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var category = new PerformanceCounterCategory("PhysicalDisk");
        string[] instances = category.GetInstanceNames().Where(n => n != "_Total").Order(StringComparer.Ordinal).ToArray();
        if (instances.Length == 0)
        {
            throw new InvalidOperationException("Keine Platten-Leistungsindikatoren vorhanden.");
        }

        var busy = new List<PerformanceCounter>();
        var latency = new List<PerformanceCounter>();
        var queue = new List<PerformanceCounter>();
        try
        {
            foreach (string instance in instances)
            {
                busy.Add(new PerformanceCounter("PhysicalDisk", "% Disk Time", instance, readOnly: true));
                latency.Add(new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", instance, readOnly: true));
                queue.Add(new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", instance, readOnly: true));
            }

            foreach (PerformanceCounter counter in busy.Concat(latency).Concat(queue))
            {
                _ = counter.NextValue();
            }

            int samples = Math.Max(1, (int)Math.Round(window.TotalSeconds));
            var busySeries = instances.Select(_ => new List<double>()).ToArray();
            var latencySeries = instances.Select(_ => new List<double>()).ToArray();
            var queueSeries = instances.Select(_ => new List<double>()).ToArray();

            for (int s = 0; s < samples; s++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < instances.Length; i++)
                {
                    busySeries[i].Add(Math.Min(100, busy[i].NextValue()));
                    latencySeries[i].Add(latency[i].NextValue());
                    queueSeries[i].Add(queue[i].NextValue());
                }
            }

            var result = new List<DiskActivityInstance>();
            for (int i = 0; i < instances.Length; i++)
            {
                result.Add(new DiskActivityInstance(
                    instances[i],
                    busySeries[i].Average(),
                    busySeries[i].Max(),
                    latencySeries[i].Average(),
                    queueSeries[i].Max()));
            }

            return new DiskActivity(TimeSpan.FromSeconds(samples), result);
        }
        finally
        {
            foreach (PerformanceCounter counter in busy.Concat(latency).Concat(queue))
            {
                counter.Dispose();
            }
        }
    }

    private static DiskMedia MapMedia(object? value) => Convert.ToInt32(value ?? 0) switch
    {
        3 => DiskMedia.Hdd,
        4 => DiskMedia.Ssd,
        5 => DiskMedia.StorageClassMemory,
        _ => DiskMedia.Unknown,
    };

    private static string MapBus(object? value) => Convert.ToInt32(value ?? 0) switch
    {
        3 => "ATA",
        7 => "USB",
        10 => "SAS",
        11 => "SATA",
        17 => "NVMe",
        var other => $"Bus {other}",
    };

    private static DiskHealth MapHealth(object? value) => Convert.ToInt32(value ?? 5) switch
    {
        0 => DiskHealth.Healthy,
        1 => DiskHealth.Warning,
        2 => DiskHealth.Unhealthy,
        _ => DiskHealth.Unknown,
    };
}
