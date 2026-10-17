using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HokiiCleaner.Core.Diagnostics;

/// <summary>Liest Arbeitsspeicher, Energieplan, Laufzeit, Neustart-Hinweise und Grafiktreiber. Rein lesend.</summary>
internal static class SystemProbe
{
    private const uint ErrorMoreData = 234;

    public static MemoryInfo ReadMemory()
    {
        var status = new NativeDiagnostics.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeDiagnostics.MemoryStatusEx>() };
        if (!NativeDiagnostics.GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Speicherstatus nicht lesbar.");
        }

        (long? allocated, long? used) = ReadPageFile();
        return new MemoryInfo(
            (long)status.TotalPhys,
            (long)status.AvailPhys,
            (long)(status.TotalPageFile - status.AvailPageFile),
            (long)status.TotalPageFile,
            allocated,
            used);
    }

    /// <summary>Die Auslagerungsdatei ist nur eine Zusatzangabe; schlägt die Abfrage fehl, fehlt die Zeile im Befund.</summary>
    private static (long? Allocated, long? Used) ReadPageFile()
    {
        try
        {
            long allocatedMb = 0;
            long usedMb = 0;
            bool any = false;
            using var searcher = new ManagementObjectSearcher("SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
            foreach (ManagementBaseObject file in searcher.Get())
            {
                using (file)
                {
                    allocatedMb += Convert.ToInt64(file["AllocatedBaseSize"] ?? 0);
                    usedMb += Convert.ToInt64(file["CurrentUsage"] ?? 0);
                    any = true;
                }
            }

            return any ? (allocatedMb * 1024 * 1024, usedMb * 1024 * 1024) : (null, null);
        }
        catch (ManagementException)
        {
            return (null, null);
        }
    }

    public static SystemConfig ReadConfig()
    {
        (string planName, Guid planId) = ReadPowerPlan();

        using RegistryKey? power = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        bool? fastStartup = power?.GetValue("HiberbootEnabled") is int value ? value != 0 : null;

        using RegistryKey? sessionManager = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        bool fileRename = sessionManager?.GetValue("PendingFileRenameOperations") is not null;

        return new SystemConfig(
            planName,
            planId,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            fastStartup,
            KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"),
            KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"),
            fileRename);
    }

    public static IReadOnlyList<DisplayDriverInfo> ReadDisplayDrivers()
    {
        var result = new List<DisplayDriverInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, Manufacturer, DriverDate, DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceClass = 'DISPLAY'");

        foreach (ManagementBaseObject driver in searcher.Get())
        {
            using (driver)
            {
                DateTime? date = driver["DriverDate"] is string text && text.Length > 0
                    ? ManagementDateTimeConverter.ToDateTime(text)
                    : null;
                result.Add(new DisplayDriverInfo(
                    Convert.ToString(driver["DeviceName"]) ?? "Anzeigegerät",
                    Convert.ToString(driver["Manufacturer"]) ?? string.Empty,
                    date,
                    Convert.ToString(driver["DriverVersion"])));
            }
        }

        return result;
    }

    private static bool KeyExists(string subKey)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey);
        return key is not null;
    }

    private static (string Name, Guid Id) ReadPowerPlan()
    {
        uint result = NativeDiagnostics.PowerGetActiveScheme(IntPtr.Zero, out IntPtr schemePointer);
        if (result != 0)
        {
            throw new Win32Exception((int)result, "Aktiver Energieplan nicht lesbar.");
        }

        try
        {
            Guid scheme = Marshal.PtrToStructure<Guid>(schemePointer);

            uint size = 0;
            result = NativeDiagnostics.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (result != 0 && result != ErrorMoreData)
            {
                throw new Win32Exception((int)result, "Name des Energieplans nicht lesbar.");
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                result = NativeDiagnostics.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
                if (result != 0)
                {
                    throw new Win32Exception((int)result, "Name des Energieplans nicht lesbar.");
                }

                return (Marshal.PtrToStringUni(buffer) ?? scheme.ToString(), scheme);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeDiagnostics.LocalFree(schemePointer);
        }
    }
}
