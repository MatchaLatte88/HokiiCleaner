using System.Management;

namespace HokiiCleaner.Core.Diagnostics;

/// <summary>Liest den Platz der Schattenkopien (Wiederherstellungspunkte). Rein lesend; braucht Administratorrechte.</summary>
internal static class ShadowStorageProbe
{
    public static ShadowStorageInfo Read()
    {
        long allocated = 0;
        long used = 0;
        long max = 0;
        bool unlimited = false;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT AllocatedSpace, UsedSpace, MaxSpace FROM Win32_ShadowStorage");
            foreach (ManagementBaseObject storage in searcher.Get())
            {
                using (storage)
                {
                    allocated += ToBytes(storage["AllocatedSpace"]);
                    used += ToBytes(storage["UsedSpace"]);

                    // MaxSpace ist ohne Limit der größte UInt64-Wert (oder 0 bzw. negativ je nach Quelle).
                    ulong limit = storage["MaxSpace"] is null ? 0 : Convert.ToUInt64(storage["MaxSpace"]);
                    if (limit == 0 || limit > long.MaxValue)
                    {
                        unlimited = true;
                    }
                    else
                    {
                        max += (long)limit;
                    }
                }
            }
        }
        catch (ManagementException ex) when (!SlownessAnalyzer.IsElevated())
        {
            // Beobachtet: ohne Administratorrechte meldet WMI hier "Initialization failure".
            throw new UnauthorizedAccessException($"WMI meldet: {ex.Message.Trim()}.", ex);
        }

        return new ShadowStorageInfo(allocated, used, unlimited ? null : max);
    }

    private static long ToBytes(object? value) => value is null ? 0 : (long)Math.Min(Convert.ToUInt64(value), long.MaxValue);
}

/// <summary>Liest alle Gerätetreiber mit Version und Datum. Rein lesend; die Abfrage dauert einige Sekunden.</summary>
internal static class DriverProbe
{
    public static IReadOnlyList<DriverInfo> ReadAll()
    {
        var result = new List<DriverInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, Manufacturer, DeviceClass, DriverDate, DriverVersion FROM Win32_PnPSignedDriver");

        foreach (ManagementBaseObject driver in searcher.Get())
        {
            using (driver)
            {
                string? name = Convert.ToString(driver["DeviceName"]);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue; // Eintrag ohne Gerätenamen ist kein sinnvoller Befund
                }

                DateTime? date = driver["DriverDate"] is string text && text.Length > 0
                    ? ManagementDateTimeConverter.ToDateTime(text)
                    : null;
                result.Add(new DriverInfo(
                    name,
                    Convert.ToString(driver["Manufacturer"]) ?? string.Empty,
                    Convert.ToString(driver["DeviceClass"]) ?? string.Empty,
                    Convert.ToString(driver["DriverVersion"]),
                    date));
            }
        }

        return result;
    }
}
