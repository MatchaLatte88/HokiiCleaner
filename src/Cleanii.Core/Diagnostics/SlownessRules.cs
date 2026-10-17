using System.Globalization;

namespace Cleanii.Core.Diagnostics;

/// <summary>
/// Macht aus Rohmessungen Befunde. Reine Funktion ohne Systemzugriff; jede Regel nennt Messwert, Schwelle und Quelle.
/// Es gibt keine Reparatur- oder Änderungsfunktion.
/// </summary>
public static class SlownessRules
{
    private const int MaxDetails = 15;
    private const int TopCount = 5;

    private sealed record ProcessGroup(string Name, int Count, double Cpu, long WorkingSet, double Io);

    public static IReadOnlyList<Check> Evaluate(SlownessSnapshot snapshot, SlownessThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SlownessThresholds t = thresholds ?? SlownessThresholds.Default;

        var checks = new List<Check>();
        checks.AddRange(DriveChecks(snapshot, t));
        checks.AddRange(StartupChecks(snapshot, t));
        checks.AddRange(ProcessChecks(snapshot, t));
        checks.AddRange(SystemChecks(snapshot, t));
        return checks;
    }

    // ---------------------------------------------------------------- Laufwerk

    private static IEnumerable<Check> DriveChecks(SlownessSnapshot s, SlownessThresholds t)
    {
        if (!s.Volumes.IsAvailable)
        {
            yield return Unavailable("drive.free", CheckArea.Drive, "Freier Platz", s.Volumes.Reason!, "DriveInfo");
        }
        else
        {
            foreach (VolumeInfo volume in s.Volumes.Value)
            {
                double free = volume.FreePercent;
                CheckOutcome outcome = free < t.FreeSpaceCriticalPercent ? CheckOutcome.Critical
                    : free < t.FreeSpaceWarnPercent ? CheckOutcome.Warning
                    : CheckOutcome.Ok;
                yield return new Check(
                    $"drive.free.{volume.Name}",
                    CheckArea.Drive,
                    outcome,
                    $"Freier Platz auf {volume.Name.TrimEnd('\\')}",
                    $"{free.ToString("N0", CultureInfo.CurrentCulture)} % frei ({ByteFormat.Format(volume.FreeBytes)} von {ByteFormat.Format(volume.TotalBytes)})",
                    $"Warnung unter {t.FreeSpaceWarnPercent:N0} %, kritisch unter {t.FreeSpaceCriticalPercent:N0} % frei",
                    "Windows braucht freien Platz für Auslagerungsdatei, Updates und temporäre Dateien. Ein fast volles Laufwerk bremst, bei SSDs kann es die Schreibgeschwindigkeit senken. Faustregel: mindestens 10 bis 15 % frei lassen.",
                    "DriveInfo",
                    Array.Empty<string>());
            }
        }

        if (!s.Disks.IsAvailable)
        {
            yield return Unavailable("drive.disks", CheckArea.Drive, "Plattentyp und Zustand", s.Disks.Reason!, "WMI MSFT_PhysicalDisk");
        }
        else
        {
            string systemDrive = s.Volumes.IsAvailable
                ? s.Volumes.Value.FirstOrDefault(v => v.IsSystem)?.Name ?? string.Empty
                : string.Empty;

            foreach (PhysicalDiskInfo disk in s.Disks.Value)
            {
                bool hostsSystem = systemDrive.Length > 0
                    && disk.DriveNames.Contains(systemDrive, StringComparer.OrdinalIgnoreCase);
                string drives = disk.DriveNames.Count == 0 ? "ohne Laufwerksbuchstaben" : "enthält " + string.Join(", ", disk.DriveNames.Select(d => d.TrimEnd('\\')));

                CheckOutcome mediaOutcome = disk.Media switch
                {
                    DiskMedia.Hdd when hostsSystem => CheckOutcome.Warning,
                    DiskMedia.Unknown => CheckOutcome.Info,
                    _ => CheckOutcome.Ok,
                };
                yield return new Check(
                    $"drive.media.{disk.Id}",
                    CheckArea.Drive,
                    mediaOutcome,
                    $"Plattentyp: {disk.Name}",
                    $"{MediaName(disk.Media)} ({disk.BusType}), {ByteFormat.Format(disk.SizeBytes)}, {drives}",
                    "Warnung, wenn Windows auf einer HDD liegt",
                    "Windows und Programme starten von einer SSD um ein Vielfaches schneller als von einer klassischen Festplatte (HDD).",
                    "WMI MSFT_PhysicalDisk und MSFT_Partition",
                    Array.Empty<string>());

                (CheckOutcome healthOutcome, string healthText) = disk.Health switch
                {
                    DiskHealth.Healthy => (CheckOutcome.Ok, "Fehlerfrei gemeldet"),
                    DiskHealth.Warning => (CheckOutcome.Warning, "Windows meldet eine Warnung"),
                    DiskHealth.Unhealthy => (CheckOutcome.Critical, "Windows meldet die Platte als fehlerhaft"),
                    _ => (CheckOutcome.Info, "Zustand unbekannt"),
                };
                yield return new Check(
                    $"drive.health.{disk.Id}",
                    CheckArea.Drive,
                    healthOutcome,
                    $"Zustand: {disk.Name}",
                    healthText,
                    "Warnung oder fehlerhaft laut Windows",
                    "Eine Platte mit Fehlern kann Zugriffe wiederholen müssen und wird dadurch langsam; zugleich besteht Datenverlust-Gefahr. Bei einer Warnung zuerst Daten sichern.",
                    "WMI MSFT_PhysicalDisk.HealthStatus",
                    Array.Empty<string>());
            }
        }

        yield return Unavailable(
            "drive.smart",
            CheckArea.Drive,
            "Temperatur und Verschleiß (S.M.A.R.T.)",
            "Braucht Administratorrechte und ist in dieser Version nicht umgesetzt.",
            "WMI MSFT_StorageReliabilityCounter");

        if (!s.ShadowStorage.IsAvailable)
        {
            yield return Unavailable("drive.shadow", CheckArea.Drive, "Schattenkopien (Wiederherstellungspunkte)", s.ShadowStorage.Reason!, "WMI Win32_ShadowStorage");
        }
        else
        {
            ShadowStorageInfo shadow = s.ShadowStorage.Value;
            string limit = shadow.MaxBytes is long max ? $", Limit {ByteFormat.Format(max)}" : ", ohne Limit";
            yield return new Check(
                "drive.shadow",
                CheckArea.Drive,
                shadow.UsedBytes >= t.ShadowStorageInfoBytes ? CheckOutcome.Info : CheckOutcome.Ok,
                "Schattenkopien (Wiederherstellungspunkte)",
                shadow.AllocatedBytes == 0 && shadow.UsedBytes == 0
                    ? "Keine Schattenkopien vorhanden"
                    : $"{ByteFormat.Format(shadow.UsedBytes)} belegt (reserviert {ByteFormat.Format(shadow.AllocatedBytes)}{limit})",
                $"Hinweis ab {ByteFormat.Format(t.ShadowStorageInfoBytes)} belegt",
                "Wiederherstellungspunkte und Schattenkopien belegen Platz auf dem Laufwerk, der in keiner Ordnerliste auftaucht. In der Speicheranalyse gehört er zum „nicht zugeordneten“ Platz.",
                "WMI Win32_ShadowStorage",
                Array.Empty<string>());
        }

        if (!s.DiskActivity.IsAvailable)
        {
            yield return Unavailable("drive.activity", CheckArea.Drive, "Auslastung der Platte", s.DiskActivity.Reason!, "Leistungsindikatoren PhysicalDisk");
        }
        else
        {
            DiskActivity activity = s.DiskActivity.Value;
            foreach (DiskActivityInstance instance in activity.Instances)
            {
                bool busy = instance.AverageBusyPercent >= t.DiskBusyWarnPercent;
                bool slow = instance.AverageLatencySeconds >= t.DiskLatencyWarnSeconds;
                yield return new Check(
                    $"drive.activity.{instance.Instance}",
                    CheckArea.Drive,
                    busy || slow ? CheckOutcome.Warning : CheckOutcome.Ok,
                    $"Auslastung: {instance.Instance}",
                    $"Ø {Pct(instance.AverageBusyPercent)} ausgelastet (Spitze {Pct(instance.MaxBusyPercent)}), Ø {instance.AverageLatencySeconds * 1000:N1} ms Antwortzeit, Warteschlange bis {instance.MaxQueueLength:N1}",
                    $"Warnung ab Ø {t.DiskBusyWarnPercent:N0} % Auslastung oder Ø {t.DiskLatencyWarnSeconds * 1000:N0} ms Antwortzeit",
                    "Ist die Platte dauerhaft ausgelastet oder antwortet langsam, warten Programme auf Daten. Das ist eine Momentaufnahme über wenige Sekunden.",
                    $"Stichprobe über {activity.Window.TotalSeconds:N0} s, Leistungsindikatoren PhysicalDisk",
                    Array.Empty<string>());
            }
        }
    }

    // ---------------------------------------------------------------- Autostart

    private static IEnumerable<Check> StartupChecks(SlownessSnapshot s, SlownessThresholds t)
    {
        if (!s.StartupEntries.IsAvailable)
        {
            yield return Unavailable("startup.count", CheckArea.Startup, "Autostart-Programme", s.StartupEntries.Reason!, "Registry, Autostart-Ordner");
        }
        else
        {
            StartupEntry[] enabled = s.StartupEntries.Value.Where(e => e.Enabled).ToArray();
            int disabledCount = s.StartupEntries.Value.Count - enabled.Length;
            CheckOutcome outcome = enabled.Length > t.StartupWarnCount ? CheckOutcome.Warning
                : enabled.Length > t.StartupInfoCount ? CheckOutcome.Info
                : CheckOutcome.Ok;
            yield return new Check(
                "startup.count",
                CheckArea.Startup,
                outcome,
                "Autostart-Programme",
                $"{enabled.Length} aktiv ({disabledCount} deaktiviert)",
                $"Hinweis ab {t.StartupInfoCount + 1}, Warnung ab {t.StartupWarnCount + 1} aktiven Einträgen",
                "Jedes Programm im Autostart verlängert den Start und belegt danach Arbeitsspeicher und Rechenzeit. Was du selten brauchst, kann beim Anmelden fehlen.",
                "Registry (Run, StartupApproved) und Autostart-Ordner",
                Limit(enabled.Select(e => $"{e.Name} ({e.Location})")));

            StartupEntry[] broken = s.StartupEntries.Value.Where(e => e.Enabled && e.ExecutableExists == false).ToArray();
            yield return new Check(
                "startup.broken",
                CheckArea.Startup,
                broken.Length > 0 ? CheckOutcome.Info : CheckOutcome.Ok,
                "Autostart-Einträge ohne Programm",
                broken.Length == 0 ? "Keine gefunden" : $"{broken.Length} verweisen auf fehlende Dateien",
                null,
                "Solche Einträge sind Überbleibsel deinstallierter Programme. Sie bremsen kaum, erzeugen aber bei jedem Start einen Fehlversuch.",
                "Registry Run-Schlüssel, Dateisystemprüfung",
                Limit(broken.Select(e => $"{e.Name}: {e.ExecutablePath}")));
        }

        if (!s.Tasks.IsAvailable)
        {
            yield return Unavailable("startup.tasks", CheckArea.Startup, "Geplante Aufgaben beim Start", s.Tasks.Reason!, "WMI MSFT_ScheduledTask");
        }
        else
        {
            ScheduledTaskEntry[] tasks = s.Tasks.Value
                .Where(x => x.Enabled && (x.AtLogon || x.AtBoot)
                    && !x.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            yield return new Check(
                "startup.tasks",
                CheckArea.Startup,
                tasks.Length > t.TasksInfoCount ? CheckOutcome.Info : CheckOutcome.Ok,
                "Geplante Aufgaben beim Start",
                $"{tasks.Length} Aufgaben von Drittanbietern starten bei Anmeldung oder Systemstart",
                $"Hinweis ab {t.TasksInfoCount + 1} Aufgaben",
                "Programme tragen sich oft zusätzlich als geplante Aufgabe ein, die im Autostart-Manager nicht auftaucht. Aufgaben im Ordner Microsoft sind nicht mitgezählt.",
                "WMI MSFT_ScheduledTask (Trigger Anmeldung und Systemstart)",
                Limit(tasks.Select(x => x.Path + x.Name)));
        }

        if (!s.Services.IsAvailable)
        {
            yield return Unavailable("startup.services", CheckArea.Startup, "Dienste mit Autostart", s.Services.Reason!, "WMI Win32_Service");
        }
        else
        {
            ServiceEntry[] services = s.Services.Value
                .Where(x => !x.PathName.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            yield return new Check(
                "startup.services",
                CheckArea.Startup,
                services.Length > t.ServicesInfoCount ? CheckOutcome.Info : CheckOutcome.Ok,
                "Dienste mit Autostart",
                $"{services.Length} außerhalb von Windows ({s.Services.Value.Count} insgesamt)",
                $"Hinweis ab {t.ServicesInfoCount + 1} Diensten außerhalb von Windows",
                "Dienste laufen unabhängig von der Anmeldung im Hintergrund. Als „außerhalb von Windows“ zählt jeder Dienst, dessen Programm nicht im Windows-Ordner liegt; das ist eine Näherung für Drittanbieter.",
                "WMI Win32_Service",
                Limit(services.Select(x => x.DisplayName)));
        }

        foreach (Check check in StartupImpactChecks(s, t))
        {
            yield return check;
        }

        if (!s.ShellExtensions.IsAvailable)
        {
            yield return Unavailable("startup.shell", CheckArea.Startup, "Erweiterungen im Kontextmenü", s.ShellExtensions.Reason!, "Registry ContextMenuHandlers");
        }
        else
        {
            ShellExtensionInfo[] all = s.ShellExtensions.Value.ToArray();
            ShellExtensionInfo[][] thirdParty = all
                .Where(e => !e.IsWindowsComponent)
                .GroupBy(e => e.Clsid, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.ToArray())
                .ToArray();
            int distinct = all.Select(e => e.Clsid.ToUpperInvariant()).Distinct().Count();
            yield return new Check(
                "startup.shell",
                CheckArea.Startup,
                thirdParty.Length > t.ShellExtensionsInfoCount ? CheckOutcome.Info : CheckOutcome.Ok,
                "Erweiterungen im Kontextmenü",
                $"{thirdParty.Length} von Drittanbietern ({distinct} insgesamt)",
                $"Hinweis ab {t.ShellExtensionsInfoCount + 1} Erweiterungen von Drittanbietern",
                "Jede Erweiterung wird beim Rechtsklick im Explorer geladen. Eine schlecht programmierte kann das Menü verzögern oder den Explorer hängen lassen; welche das tut, lässt sich hier nicht messen. Als Drittanbieter zählt jede Erweiterung, deren DLL nicht im Windows-Ordner liegt.",
                "Registry (HKCR … shellex\\ContextMenuHandlers, CLSID\\InprocServer32)",
                Limit(thirdParty.Select(g => $"{g[0].Name} ({Path.GetFileName(g[0].Library ?? "?")})")));
        }

        if (!s.BrowserExtensions.IsAvailable)
        {
            yield return Unavailable("startup.browserext", CheckArea.Startup, "Browser-Erweiterungen", s.BrowserExtensions.Reason!, "Profilordner der Browser");
        }
        else
        {
            BrowserExtensionInfo[] extensions = s.BrowserExtensions.Value.ToArray();
            string perBrowser = extensions.Length == 0
                ? "keine"
                : string.Join(", ", extensions.GroupBy(e => e.Browser).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
            yield return new Check(
                "startup.browserext",
                CheckArea.Startup,
                extensions.Length > t.BrowserExtensionsInfoCount ? CheckOutcome.Info : CheckOutcome.Ok,
                "Browser-Erweiterungen",
                $"{extensions.Length} installiert ({perBrowser})",
                $"Hinweis ab {t.BrowserExtensionsInfoCount + 1} Erweiterungen",
                "Aktive Erweiterungen laufen im Browser mit und können Seiten verlangsamen und Arbeitsspeicher belegen. Bei Chrome, Edge, Brave und Vivaldi werden alle installierten gezählt, auch deaktivierte; bei Firefox nur aktive vom Benutzer installierte. Opera und der Firefox-Standard werden nicht erfasst.",
                "Profilordner der Browser (Extensions, extensions.json)",
                Limit(extensions.Select(e => $"{e.Browser} ({e.Profile}): {e.Name}")));
        }

        yield return Unavailable(
            "startup.boottime",
            CheckArea.Startup,
            "Dauer des Systemstarts",
            "Steht im Ereignisprotokoll Diagnostics-Performance, das Administratorrechte braucht. In dieser Version nicht umgesetzt.",
            "Ereignisprotokoll Microsoft-Windows-Diagnostics-Performance");
    }

    private enum Impact
    {
        NotRunning,
        NotMeasured,
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// Stuft die aktiven Autostart-Programme nach ihrem Verbrauch in der Stichprobe ein. Das ist die Last im Betrieb,
    /// nicht die Verlängerung des Starts. Zugeordnet wird über den Prozessnamen (Dateiname ohne Endung).
    /// </summary>
    private static IEnumerable<Check> StartupImpactChecks(SlownessSnapshot s, SlownessThresholds t)
    {
        if (!s.StartupEntries.IsAvailable || !s.Processes.IsAvailable)
        {
            string reason = !s.StartupEntries.IsAvailable ? s.StartupEntries.Reason! : s.Processes.Reason!;
            yield return Unavailable("startup.impact", CheckArea.Startup, "Last der Autostart-Programme", reason, "Autostart-Einträge und Prozess-Stichprobe");
            yield break;
        }

        Dictionary<string, ProcessGroup> running = Group(s.Processes.Value)
            .ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> measuredByName = s.Processes.Value.Processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(p => p.CpuMeasured), StringComparer.OrdinalIgnoreCase);

        var rated = new List<(string Name, Impact Impact, ProcessGroup? Group)>();
        foreach (StartupEntry entry in s.StartupEntries.Value.Where(e => e.Enabled && e.ExecutablePath is not null))
        {
            string processName = Path.GetFileNameWithoutExtension(entry.ExecutablePath!);
            if (rated.Any(r => string.Equals(r.Name, processName, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // mehrere Einträge für dasselbe Programm
            }

            if (!running.TryGetValue(processName, out ProcessGroup? group))
            {
                rated.Add((processName, Impact.NotRunning, null));
            }
            else if (!measuredByName[processName])
            {
                rated.Add((processName, Impact.NotMeasured, group));
            }
            else
            {
                Impact impact = group.Cpu >= t.ImpactHighCpuPercent || group.Io >= t.ImpactHighIoBytesPerSecond || group.WorkingSet >= t.ImpactHighMemoryBytes
                    ? Impact.High
                    : group.Cpu >= t.ImpactMediumCpuPercent || group.Io >= t.ImpactMediumIoBytesPerSecond || group.WorkingSet >= t.ImpactMediumMemoryBytes
                        ? Impact.Medium
                        : Impact.Low;
                rated.Add((processName, impact, group));
            }
        }

        int Count(Impact impact) => rated.Count(r => r.Impact == impact);
        string source = $"Autostart-Einträge (Registry, Ordner) und Prozess-Stichprobe über {s.Processes.Value.Window.TotalSeconds:N0} s";
        yield return new Check(
            "startup.impact",
            CheckArea.Startup,
            Count(Impact.High) > 0 ? CheckOutcome.Info : CheckOutcome.Ok,
            "Last der Autostart-Programme",
            rated.Count == 0
                ? "Keine aktiven Autostart-Programme mit bekannter Datei"
                : $"{Count(Impact.High)} hoch, {Count(Impact.Medium)} mittel, {Count(Impact.Low)} niedrig, {Count(Impact.NotRunning)} laufen nicht, {Count(Impact.NotMeasured)} nicht messbar",
            $"Hoch ab {t.ImpactHighCpuPercent:N0} % Prozessor, {ByteFormat.Format((long)t.ImpactHighIoBytesPerSecond)}/s Zugriffen oder {ByteFormat.Format(t.ImpactHighMemoryBytes)} Arbeitsspeicher; mittel ab {t.ImpactMediumCpuPercent:N0} %, {ByteFormat.Format((long)t.ImpactMediumIoBytesPerSecond)}/s oder {ByteFormat.Format(t.ImpactMediumMemoryBytes)}",
            "Eingestuft wird, was das Programm in der Stichprobe gerade verbraucht, nicht wie stark es den Start verlängert; die Startdauer je Programm steht nur mit Administratorrechten im Ereignisprotokoll und ist nicht umgesetzt. Programme, die sich nach dem Start beenden, erscheinen als „läuft nicht“. Prozesse gleichen Namens sind zusammengefasst.",
            source,
            Limit(rated
                .Where(r => r.Impact >= Impact.NotMeasured)
                .OrderByDescending(r => r.Impact)
                .ThenByDescending(r => r.Group?.WorkingSet ?? 0)
                .Select(r => r.Impact == Impact.NotMeasured
                    ? $"{r.Name}: nicht messbar (Zugriff verweigert)"
                    : $"{r.Name}: {ImpactName(r.Impact)} ({Pct(r.Group!.Cpu)} Prozessor, {ByteFormat.Format(r.Group.WorkingSet)} Arbeitsspeicher, {ByteFormat.Format((long)r.Group.Io)}/s Zugriffe)")));
    }

    private static string ImpactName(Impact impact) => impact switch
    {
        Impact.High => "hoch",
        Impact.Medium => "mittel",
        Impact.Low => "niedrig",
        _ => "unbekannt",
    };

    // ---------------------------------------------------------------- Prozesse und Speicher

    private static IEnumerable<Check> ProcessChecks(SlownessSnapshot s, SlownessThresholds t)
    {
        if (!s.Processes.IsAvailable)
        {
            yield return Unavailable("proc.cpu", CheckArea.Processes, "Prozessor-Auslastung", s.Processes.Reason!, "Prozess-Stichprobe");
            yield return Unavailable("proc.io", CheckArea.Processes, "Festplatten-Zugriffe der Programme", s.Processes.Reason!, "Prozess-Stichprobe");
        }
        else
        {
            ProcessSampleResult sample = s.Processes.Value;
            List<ProcessGroup> groups = Group(sample);
            string source = $"Stichprobe über {sample.Window.TotalSeconds:N0} s";

            double cpu = sample.SystemCpuPercent;
            yield return new Check(
                "proc.cpu",
                CheckArea.Processes,
                cpu >= t.CpuCriticalPercent ? CheckOutcome.Critical : cpu >= t.CpuWarnPercent ? CheckOutcome.Warning : CheckOutcome.Ok,
                "Prozessor-Auslastung",
                $"Ø {Pct(cpu)} über {sample.Window.TotalSeconds:N0} s",
                $"Warnung ab {t.CpuWarnPercent:N0} %, kritisch ab {t.CpuCriticalPercent:N0} %",
                "Ist der Prozessor dauerhaft ausgelastet, müssen alle anderen Programme warten. Die Liste zeigt, was die Rechenzeit verbraucht. Eine kurze Stichprobe kann Lastspitzen verpassen.",
                source,
                groups.Where(g => g.Cpu > 0).OrderByDescending(g => g.Cpu).Take(TopCount)
                    .Select(g => $"{Label(g)}: {Pct(g.Cpu)} Prozessor").ToArray());

            ProcessGroup? topIo = groups.OrderByDescending(g => g.Io).FirstOrDefault();
            bool anyIo = sample.Processes.Any(p => p.IoMeasured);
            if (!anyIo)
            {
                yield return Unavailable("proc.io", CheckArea.Processes, "Festplatten-Zugriffe der Programme", "Kein Prozess lieferte Zugriffszahlen.", source);
            }
            else
            {
                yield return new Check(
                    "proc.io",
                    CheckArea.Processes,
                    topIo is not null && topIo.Io >= t.IoInfoBytesPerSecond ? CheckOutcome.Info : CheckOutcome.Ok,
                    "Festplatten-Zugriffe der Programme",
                    topIo is null || topIo.Io <= 0
                        ? "Keine nennenswerten Zugriffe"
                        : $"Größter Verbraucher: {Label(topIo)} mit {ByteFormat.Format((long)topIo.Io)}/s",
                    $"Hinweis ab {ByteFormat.Format((long)t.IoInfoBytesPerSecond)}/s eines Programms",
                    "Programme, die ständig lesen oder schreiben (Indexer, Sync, Virenscanner, Updates), halten die Platte beschäftigt und verlangsamen alles, was gleichzeitig Daten braucht.",
                    source,
                    groups.Where(g => g.Io > 0).OrderByDescending(g => g.Io).Take(TopCount)
                        .Select(g => $"{Label(g)}: {ByteFormat.Format((long)g.Io)}/s").ToArray());
            }

            int measured = sample.Processes.Count(p => p.CpuMeasured);
            int total = sample.Processes.Count;
            bool lowCoverage = total > 0 && (double)measured / total < t.ProcessCoverageMinimum;
            yield return new Check(
                "proc.coverage",
                CheckArea.Processes,
                lowCoverage ? CheckOutcome.Info : CheckOutcome.Ok,
                "Messabdeckung der Prozesse",
                $"{measured} von {total} Prozessen messbar",
                $"Hinweis unter {t.ProcessCoverageMinimum * 100:N0} % messbarer Prozesse",
                "Ohne Administratorrechte sind Systemprozesse und Dienste oft gesperrt. Ihre Last fehlt dann in den Listen, die Gesamtauslastung ist trotzdem vollständig.",
                source,
                Array.Empty<string>());
        }

        if (!s.Memory.IsAvailable)
        {
            yield return Unavailable("proc.memory", CheckArea.Processes, "Arbeitsspeicher", s.Memory.Reason!, "GlobalMemoryStatusEx");
            yield break;
        }

        MemoryInfo memory = s.Memory.Value;
        double availablePercent = memory.AvailablePercent;
        List<ProcessGroup>? memGroups = s.Processes.IsAvailable ? Group(s.Processes.Value) : null;
        yield return new Check(
            "proc.memory",
            CheckArea.Processes,
            availablePercent < t.MemoryAvailableCriticalPercent ? CheckOutcome.Critical
                : availablePercent < t.MemoryAvailableWarnPercent ? CheckOutcome.Warning
                : CheckOutcome.Ok,
            "Arbeitsspeicher",
            $"{ByteFormat.Format(memory.AvailablePhysicalBytes)} von {ByteFormat.Format(memory.TotalPhysicalBytes)} frei ({availablePercent:N0} %)",
            $"Warnung unter {t.MemoryAvailableWarnPercent:N0} %, kritisch unter {t.MemoryAvailableCriticalPercent:N0} % frei",
            "Wird der Arbeitsspeicher knapp, lagert Windows Daten auf die Platte aus, und alles wird spürbar langsamer. Die Liste zeigt den Arbeitssatz je Programm; geteilter Speicher kann dabei mehrfach auftauchen.",
            "GlobalMemoryStatusEx",
            memGroups is null
                ? Array.Empty<string>()
                : memGroups.OrderByDescending(g => g.WorkingSet).Take(TopCount)
                    .Select(g => $"{Label(g)}: {ByteFormat.Format(g.WorkingSet)}").ToArray());

        double commit = memory.CommitPercent;
        var commitDetails = new List<string>
        {
            $"Zugesagt: {ByteFormat.Format(memory.CommitTotalBytes)} von {ByteFormat.Format(memory.CommitLimitBytes)}",
        };
        if (memory.PageFileAllocatedBytes is long allocated && memory.PageFileUsedBytes is long used)
        {
            commitDetails.Add($"Auslagerungsdatei: {ByteFormat.Format(used)} von {ByteFormat.Format(allocated)} genutzt");
        }

        yield return new Check(
            "proc.commit",
            CheckArea.Processes,
            commit >= t.CommitCriticalPercent ? CheckOutcome.Critical : commit >= t.CommitWarnPercent ? CheckOutcome.Warning : CheckOutcome.Ok,
            "Speicherzusagen (Commit)",
            $"{commit:N0} % des Limits aus Arbeitsspeicher und Auslagerungsdatei zugesagt",
            $"Warnung ab {t.CommitWarnPercent:N0} %, kritisch ab {t.CommitCriticalPercent:N0} %",
            "Das Limit setzt sich aus Arbeitsspeicher und Auslagerungsdatei zusammen. Wird es erreicht, schlagen Speicheranforderungen fehl und Programme können abstürzen.",
            "GlobalMemoryStatusEx, WMI Win32_PageFileUsage",
            commitDetails);
    }

    // ---------------------------------------------------------------- System

    private static IEnumerable<Check> SystemChecks(SlownessSnapshot s, SlownessThresholds t)
    {
        if (!s.Config.IsAvailable)
        {
            yield return Unavailable("sys.config", CheckArea.System, "Systemeinstellungen", s.Config.Reason!, "Energieoptionen, Registry");
        }
        else
        {
            SystemConfig config = s.Config.Value;

            bool powerSaver = config.PowerPlanId == KnownPowerPlans.PowerSaver;
            yield return new Check(
                "sys.power",
                CheckArea.System,
                powerSaver ? CheckOutcome.Warning : CheckOutcome.Ok,
                "Energiesparplan",
                $"Aktiv: {config.PowerPlanName}",
                "Warnung beim Plan „Energiesparmodus“",
                "Der Plan „Energiesparmodus“ begrenzt die Prozessorleistung und macht den Rechner spürbar träger. Benutzerdefinierte Pläne der Hersteller können Ähnliches tun, sind aber nicht erkennbar.",
                "Energieoptionen (powrprof)",
                Array.Empty<string>());

            double days = config.Uptime.TotalDays;
            string fast = config.FastStartupEnabled == true
                ? " Der Schnellstart ist aktiv: Herunterfahren und wieder Einschalten setzt die Laufzeit nicht zurück, nur ein Neustart tut das."
                : string.Empty;
            yield return new Check(
                "sys.uptime",
                CheckArea.System,
                days >= t.UptimeWarnDays ? CheckOutcome.Warning : days >= t.UptimeInfoDays ? CheckOutcome.Info : CheckOutcome.Ok,
                "Laufzeit seit dem letzten Neustart",
                FormatUptime(config.Uptime),
                $"Hinweis ab {t.UptimeInfoDays:N0} Tagen, Warnung ab {t.UptimeWarnDays:N0} Tagen",
                "Nach langer Laufzeit sammeln sich Speicherlecks und Hintergrundlast, ein Neustart räumt das auf." + fast,
                "Systemlaufzeit (Environment.TickCount64), Registry Power",
                Array.Empty<string>());

            bool updatePending = config.WindowsUpdateRebootPending || config.ServicingRebootPending;
            yield return new Check(
                "sys.reboot",
                CheckArea.System,
                updatePending ? CheckOutcome.Warning : config.FileRenamePending ? CheckOutcome.Info : CheckOutcome.Ok,
                "Ausstehender Neustart",
                updatePending ? "Ein Neustart für Updates ist ausstehend"
                    : config.FileRenamePending ? "Dateien warten auf Austausch beim nächsten Start"
                    : "Keiner ausstehend",
                null,
                "Ein ausstehender Neustart nach Updates lässt Windows in einem Zwischenzustand; Updates sind dann nicht vollständig wirksam. Wartende Dateiaustausche sind meist harmlos.",
                "Registry (WindowsUpdate, Component Based Servicing, Session Manager)",
                Array.Empty<string>());
        }

        yield return DriverAgeCheck(s, t);

        if (!s.DisplayDrivers.IsAvailable)
        {
            yield return Unavailable("sys.gpu", CheckArea.System, "Grafiktreiber", s.DisplayDrivers.Reason!, "WMI Win32_PnPSignedDriver");
            yield break;
        }

        IReadOnlyList<DisplayDriverInfo> drivers = s.DisplayDrivers.Value;
        if (drivers.Count == 0)
        {
            yield return new Check(
                "sys.gpu", CheckArea.System, CheckOutcome.Info, "Grafiktreiber", "Kein Anzeigegerät gefunden", null,
                "Ohne erkanntes Anzeigegerät lässt sich der Treiber nicht prüfen.", "WMI Win32_PnPSignedDriver", Array.Empty<string>());
            yield break;
        }

        foreach (DisplayDriverInfo driver in drivers)
        {
            bool basic = driver.Manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                || driver.DeviceName.Contains("Basic", StringComparison.OrdinalIgnoreCase);
            double? ageDays = driver.DriverDate is DateTime date ? (DateTime.Now - date).TotalDays : null;

            CheckOutcome outcome = basic ? CheckOutcome.Warning
                : ageDays is double age && age > t.DriverAgeInfoDays ? CheckOutcome.Info
                : CheckOutcome.Ok;
            string measurement = basic
                ? $"{driver.DeviceName}: Standardtreiber von Microsoft"
                : $"{driver.DeviceName}: Treiber {driver.Version} vom {driver.DriverDate:d}";
            yield return new Check(
                $"sys.gpu.{driver.DeviceName}",
                CheckArea.System,
                outcome,
                "Grafiktreiber",
                measurement,
                $"Warnung beim Microsoft-Standardtreiber, Hinweis ab {t.DriverAgeInfoDays / 365:N0} Jahren Alter",
                "Mit dem Standardtreiber fehlt die Hardware-Beschleunigung des Herstellers, Oberfläche und Spiele laufen langsamer. Ein altes Treiberdatum allein ist kein Fehler.",
                "WMI Win32_PnPSignedDriver",
                Array.Empty<string>());
        }
    }

    /// <summary>Gerätetreiber von Drittanbietern, die älter als die Schwelle sind. Nur lokal gemessen: Ob es neuere gibt, wird nicht geprüft.</summary>
    private static Check DriverAgeCheck(SlownessSnapshot s, SlownessThresholds t)
    {
        if (!s.Drivers.IsAvailable)
        {
            return Unavailable("sys.drivers", CheckArea.System, "Alter der Gerätetreiber", s.Drivers.Reason!, "WMI Win32_PnPSignedDriver");
        }

        // Treiber, deren Alter für Tempo und Stabilität am meisten zählt: Grafik, Netzwerk, Datenträger-Controller.
        string[] relevantClasses = { "DISPLAY", "NET", "SCSIADAPTER", "HDC" };

        DriverInfo[] candidates = s.Drivers.Value
            .Where(d => d.Date is not null
                && !d.Manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                && !d.Manufacturer.StartsWith("(Standard", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Windows liefert Treiber von Herstellern mit Standarddatum (21.6.2006), Platzhaltern (z. B. 1968) und der eigenen
        // Versionsnummer (10.0.…) mit. Ihr Datum sagt nichts über das Alter, daher zählen sie nicht.
        DriverInfo[] thirdParty = candidates.Where(HasMeaningfulDate).ToArray();
        int withoutMeaningfulDate = candidates.Length - thirdParty.Length;
        DriverInfo[] old = thirdParty
            .Where(d => (DateTime.Now - d.Date!.Value).TotalDays > t.OldDriverInfoDays)
            .OrderBy(d => d.Date)
            .ToArray();
        int relevantOld = old.Count(d => relevantClasses.Contains(d.DeviceClass, StringComparer.OrdinalIgnoreCase));

        return new Check(
            "sys.drivers",
            CheckArea.System,
            relevantOld > 0 ? CheckOutcome.Info : CheckOutcome.Ok,
            "Alter der Gerätetreiber",
            $"{old.Length} von {thirdParty.Length} Treibern von Drittanbietern sind älter als {t.OldDriverInfoDays / 365:N0} Jahre ({relevantOld} bei Grafik, Netzwerk oder Datenträger-Controllern)"
                + (withoutMeaningfulDate > 0 ? $"; {withoutMeaningfulDate} mit Standard- oder Platzhalterdatum nicht gewertet" : string.Empty),
            $"Hinweis, wenn ein Grafik-, Netzwerk- oder Controller-Treiber älter als {t.OldDriverInfoDays / 365:N0} Jahre ist",
            "Ein altes Treiberdatum allein ist kein Fehler: Viele Geräte brauchen keinen neueren Treiber. Bei Grafik, Netzwerk und Datenträger-Controllern lohnt ein Blick auf die Herstellerseite. Cleanii prüft nicht, ob es einen neueren Treiber gibt, und lädt nichts herunter.",
            "WMI Win32_PnPSignedDriver (nur lokale Angaben)",
            Limit(old.Select(d => $"{d.DeviceName} ({d.DeviceClass}): {d.Version}, {d.Date:d}")));
    }

    private static readonly DateTime WindowsInboxDriverDate = new(2006, 6, 21);

    private static bool HasMeaningfulDate(DriverInfo driver)
        => driver.Date is DateTime date
            && date.Year >= 1995
            && date.Date != WindowsInboxDriverDate
            && driver.Version?.StartsWith("10.0.", StringComparison.Ordinal) != true;

    // ---------------------------------------------------------------- Hilfen

    private static Check Unavailable(string id, CheckArea area, string title, string reason, string source)
        => new(id, area, CheckOutcome.Unavailable, title, "Nicht gemessen", null, reason, source, Array.Empty<string>());

    private static List<ProcessGroup> Group(ProcessSampleResult sample)
        => sample.Processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessGroup(g.Key, g.Count(), g.Sum(p => p.CpuPercent), g.Sum(p => p.WorkingSetBytes), g.Sum(p => p.IoBytesPerSecond)))
            .ToList();

    private static string Label(ProcessGroup group)
        => group.Count > 1 ? $"{group.Name} ({group.Count} Prozesse)" : group.Name;

    private static IReadOnlyList<string> Limit(IEnumerable<string> items)
    {
        string[] all = items.ToArray();
        if (all.Length <= MaxDetails)
        {
            return all;
        }

        return all.Take(MaxDetails).Append($"… und {all.Length - MaxDetails} weitere").ToArray();
    }

    private static string Pct(double value)
        => value < 10 ? value.ToString("N1", CultureInfo.CurrentCulture) + " %" : value.ToString("N0", CultureInfo.CurrentCulture) + " %";

    private static string MediaName(DiskMedia media) => media switch
    {
        DiskMedia.Hdd => "HDD",
        DiskMedia.Ssd => "SSD",
        DiskMedia.StorageClassMemory => "Speicherklassen-Speicher",
        _ => "Typ unbekannt",
    };

    private static string FormatUptime(TimeSpan uptime)
        => uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays} Tage {uptime.Hours} Std."
            : $"{uptime.Hours} Std. {uptime.Minutes} Min.";
}

/// <summary>Bekannte Energiesparplan-Kennungen von Windows.</summary>
public static class KnownPowerPlans
{
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
}
