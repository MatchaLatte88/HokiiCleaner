using System.Management;
using Microsoft.Win32;

namespace Cleanii.Core.Diagnostics;

/// <summary>Liest Autostart-Einträge, geplante Aufgaben und Dienste. Rein lesend, es wird nichts aktiviert oder deaktiviert.</summary>
internal static class StartupProbe
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static IReadOnlyList<StartupEntry> ReadEntries()
    {
        var entries = new List<StartupEntry>();
        entries.AddRange(ReadRunKey(RegistryHive.CurrentUser, RegistryView.Registry64, "Run", "Benutzer, Run"));
        entries.AddRange(ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry64, "Run", "Alle Benutzer, Run"));
        entries.AddRange(ReadRunKey(RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", "Alle Benutzer, 32-Bit-Run"));
        entries.AddRange(ReadFolder(Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, "Autostart-Ordner (Benutzer)"));
        entries.AddRange(ReadFolder(Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, "Autostart-Ordner (alle)"));
        return entries;
    }

    private static IEnumerable<StartupEntry> ReadRunKey(RegistryHive hive, RegistryView view, string approvalName, string location)
    {
        var result = new List<StartupEntry>();

        using RegistryKey runBase = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? run = runBase.OpenSubKey(RunKey);
        if (run is null)
        {
            return result;
        }

        // Die Freigaben liegen immer in der 64-Bit-Sicht, auch für 32-Bit-Einträge (Unterschlüssel Run32).
        using RegistryKey approvalBase = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? approved = approvalBase.OpenSubKey($@"{ApprovedKey}\{approvalName}");

        foreach (string name in run.GetValueNames())
        {
            string command = run.GetValue(name) as string ?? string.Empty;
            string? executable = StartupCommandParser.ResolveExecutable(command);
            result.Add(new StartupEntry(
                name,
                command,
                location,
                StartupApproval.IsEnabled(approved?.GetValue(name) as byte[]),
                executable,
                StartupCommandParser.Exists(executable)));
        }

        return result;
    }

    private static IEnumerable<StartupEntry> ReadFolder(Environment.SpecialFolder folder, RegistryHive hive, string location)
    {
        string path = Environment.GetFolderPath(folder);
        if (!Directory.Exists(path))
        {
            return Array.Empty<StartupEntry>();
        }

        using RegistryKey approvalBase = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey? approved = approvalBase.OpenSubKey($@"{ApprovedKey}\StartupFolder");

        var result = new List<StartupEntry>();
        foreach (string file in Directory.EnumerateFiles(path))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Bei Verknüpfungen (.lnk) wird nur die Datei selbst geprüft, nicht ihr Ziel.
            result.Add(new StartupEntry(
                name, file, location, StartupApproval.IsEnabled(approved?.GetValue(name) as byte[]), file, true));
        }

        return result;
    }

    public static IReadOnlyList<ScheduledTaskEntry> ReadTasks()
    {
        var result = new List<ScheduledTaskEntry>();
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\TaskScheduler",
            "SELECT TaskName, TaskPath, State, Triggers, Actions FROM MSFT_ScheduledTask");

        foreach (ManagementBaseObject task in searcher.Get())
        {
            using (task)
            {
                var triggers = (task["Triggers"] as ManagementBaseObject[]) ?? Array.Empty<ManagementBaseObject>();
                string[] triggerClasses = triggers.Select(t => Convert.ToString(t.SystemProperties["__CLASS"].Value) ?? string.Empty).ToArray();

                string? command = null;
                if (task["Actions"] is ManagementBaseObject[] actions)
                {
                    foreach (ManagementBaseObject action in actions)
                    {
                        // Nicht jede Aktion hat "Execute" (z. B. COM-Handler); der Indexer würde dann werfen.
                        string? execute = FindProperty(action, "Execute");
                        if (execute is not null)
                        {
                            command = $"{execute} {FindProperty(action, "Arguments")}".Trim();
                            break;
                        }
                    }
                }

                result.Add(new ScheduledTaskEntry(
                    Convert.ToString(task["TaskName"]) ?? string.Empty,
                    Convert.ToString(task["TaskPath"]) ?? string.Empty,
                    Convert.ToInt32(task["State"] ?? 0) != 1, // 1 = deaktiviert
                    triggerClasses.Contains("MSFT_TaskLogonTrigger"),
                    triggerClasses.Contains("MSFT_TaskBootTrigger"),
                    command));
            }
        }

        return result;
    }

    private static string? FindProperty(ManagementBaseObject source, string name)
    {
        foreach (PropertyData property in source.Properties)
        {
            if (property.Name == name)
            {
                return Convert.ToString(property.Value);
            }
        }

        return null;
    }

    public static IReadOnlyList<ServiceEntry> ReadAutoStartServices()
    {
        var result = new List<ServiceEntry>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DisplayName, PathName, State, DelayedAutoStart FROM Win32_Service WHERE StartMode = 'Auto'");

        foreach (ManagementBaseObject service in searcher.Get())
        {
            using (service)
            {
                result.Add(new ServiceEntry(
                    Convert.ToString(service["Name"]) ?? string.Empty,
                    Convert.ToString(service["DisplayName"]) ?? Convert.ToString(service["Name"]) ?? string.Empty,
                    Convert.ToString(service["PathName"]) ?? string.Empty,
                    string.Equals(Convert.ToString(service["State"]), "Running", StringComparison.OrdinalIgnoreCase),
                    service["DelayedAutoStart"] is true));
            }
        }

        return result;
    }
}
