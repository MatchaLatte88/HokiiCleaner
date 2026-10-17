using Cleanii.Core.Diagnostics;

namespace Cleanii.Tests;

/// <summary>Regeln für Schattenkopien, Erweiterungen, Last der Autostart-Programme und Treiberalter.</summary>
public class TakeoverRulesTests
{
    private static Measured<T> No<T>() => Measured<T>.Unavailable("im Test nicht gemessen");

    private static Measured<T> Yes<T>(T value) => Measured<T>.Of(value);

    private static SlownessSnapshot Empty() => new(
        false,
        No<IReadOnlyList<VolumeInfo>>(), No<IReadOnlyList<PhysicalDiskInfo>>(), No<DiskActivity>(),
        No<IReadOnlyList<StartupEntry>>(), No<IReadOnlyList<ScheduledTaskEntry>>(), No<IReadOnlyList<ServiceEntry>>(),
        No<ProcessSampleResult>(), No<MemoryInfo>(), No<SystemConfig>(), No<IReadOnlyList<DisplayDriverInfo>>(),
        No<IReadOnlyList<ShellExtensionInfo>>(), No<IReadOnlyList<BrowserExtensionInfo>>(), No<IReadOnlyList<DriverInfo>>(),
        No<ShadowStorageInfo>());

    private static Check Find(SlownessSnapshot snapshot, string id)
        => Assert.Single(SlownessRules.Evaluate(snapshot), c => c.Id == id);

    private const long Gb = 1024L * 1024 * 1024;

    // ------------------------------------------------------------ Schattenkopien

    [Fact]
    public void Shadow_storage_is_unavailable_with_the_reason_when_not_measured()
    {
        Check check = Find(Empty(), "drive.shadow");

        Assert.Equal(CheckOutcome.Unavailable, check.Outcome);
        Assert.Contains("im Test nicht gemessen", check.Explanation);
    }

    [Theory]
    [InlineData(0, CheckOutcome.Ok)]
    [InlineData(19, CheckOutcome.Ok)]
    [InlineData(20, CheckOutcome.Info)]
    [InlineData(80, CheckOutcome.Info)]
    public void Shadow_storage_follows_the_threshold(long usedGb, CheckOutcome expected)
    {
        var snapshot = Empty() with { ShadowStorage = Yes(new ShadowStorageInfo(usedGb * Gb, usedGb * Gb, 100 * Gb)) };

        Check check = Find(snapshot, "drive.shadow");

        Assert.Equal(expected, check.Outcome);
        Assert.NotNull(check.Threshold);
    }

    [Fact]
    public void Shadow_storage_says_so_when_there_are_no_copies_and_names_a_missing_limit()
    {
        Check none = Find(Empty() with { ShadowStorage = Yes(new ShadowStorageInfo(0, 0, null)) }, "drive.shadow");
        Check unlimited = Find(Empty() with { ShadowStorage = Yes(new ShadowStorageInfo(30 * Gb, 25 * Gb, null)) }, "drive.shadow");

        Assert.Contains("Keine Schattenkopien", none.Measurement);
        Assert.Contains("ohne Limit", unlimited.Measurement);
    }

    // ------------------------------------------------------------ Kontextmenü und Browser

    private static ShellExtensionInfo Shell(string name, bool windows = false, string? clsid = null, string scope = "Dateien")
        => new(scope, name, clsid ?? Guid.NewGuid().ToString("B"), windows ? @"C:\Windows\System32\x.dll" : $@"C:\Program Files\{name}\{name}.dll", windows);

    [Fact]
    public void Shell_extensions_count_only_third_party_and_each_component_once()
    {
        var shared = Guid.NewGuid().ToString("B");
        var list = new List<ShellExtensionInfo>
        {
            Shell("WinThing", windows: true),
            Shell("Zip", clsid: shared, scope: "Dateien"),
            Shell("Zip", clsid: shared, scope: "Ordner"), // dieselbe Komponente an zwei Orten
        };
        for (int i = 0; i < 10; i++)
        {
            list.Add(Shell($"Ext{i}"));
        }

        Check check = Find(Empty() with { ShellExtensions = Yes<IReadOnlyList<ShellExtensionInfo>>(list) }, "startup.shell");

        Assert.Equal(CheckOutcome.Info, check.Outcome); // 11 Drittanbieter > 10
        Assert.Contains("11 von Drittanbietern", check.Measurement);
        Assert.DoesNotContain(check.Details, d => d.Contains("WinThing"));
    }

    [Fact]
    public void Shell_extensions_up_to_the_threshold_are_unremarkable()
    {
        var list = Enumerable.Range(0, 10).Select(i => Shell($"Ext{i}")).ToList();

        Check check = Find(Empty() with { ShellExtensions = Yes<IReadOnlyList<ShellExtensionInfo>>(list) }, "startup.shell");

        Assert.Equal(CheckOutcome.Ok, check.Outcome);
    }

    [Theory]
    [InlineData(15, CheckOutcome.Ok)]
    [InlineData(16, CheckOutcome.Info)]
    public void Browser_extensions_follow_the_threshold_and_are_listed_per_browser(int count, CheckOutcome expected)
    {
        var list = Enumerable.Range(0, count)
            .Select(i => new BrowserExtensionInfo(i % 2 == 0 ? "Chrome" : "Edge", "Default", $"Erweiterung {i}", $"id{i}"))
            .ToList();

        Check check = Find(Empty() with { BrowserExtensions = Yes<IReadOnlyList<BrowserExtensionInfo>>(list) }, "startup.browserext");

        Assert.Equal(expected, check.Outcome);
        Assert.Contains("Chrome", check.Measurement);
        Assert.Contains("Edge", check.Measurement);
    }

    [Fact]
    public void Browser_extensions_without_any_are_reported_as_none()
    {
        Check check = Find(Empty() with { BrowserExtensions = Yes<IReadOnlyList<BrowserExtensionInfo>>(Array.Empty<BrowserExtensionInfo>()) }, "startup.browserext");

        Assert.Equal(CheckOutcome.Ok, check.Outcome);
        Assert.Contains("0 installiert", check.Measurement);
    }

    // ------------------------------------------------------------ Last der Autostart-Programme

    private static StartupEntry Startup(string exe, bool enabled = true)
        => new(exe, $@"C:\Apps\{exe}.exe", "Benutzer, Run", enabled, $@"C:\Apps\{exe}.exe", true);

    private static ProcessSample Proc(string name, double cpu = 0, long ws = 0, double io = 0, bool measured = true, int pid = 1)
        => new(pid, name, cpu, ws, io, measured, measured);

    private static SlownessSnapshot WithStartup(IEnumerable<StartupEntry> entries, params ProcessSample[] processes)
        => Empty() with
        {
            StartupEntries = Yes<IReadOnlyList<StartupEntry>>(entries.ToArray()),
            Processes = Yes(new ProcessSampleResult(TimeSpan.FromSeconds(5), 10, processes)),
        };

    [Fact]
    public void Impact_is_rated_from_the_measured_use_and_matched_by_process_name()
    {
        var snapshot = WithStartup(
            new[] { Startup("Heavy"), Startup("Middle"), Startup("Light"), Startup("Gone"), Startup("Locked"), Startup("Disabled", enabled: false) },
            Proc("heavy", cpu: 6),
            Proc("MIDDLE", cpu: 0.1, ws: 200L * 1024 * 1024),
            Proc("Light", cpu: 0.1, ws: 10L * 1024 * 1024, io: 1000),
            Proc("Locked", measured: false),
            Proc("Disabled", cpu: 50));

        Check check = Find(snapshot, "startup.impact");

        Assert.Equal(CheckOutcome.Info, check.Outcome);
        Assert.Equal("1 hoch, 1 mittel, 1 niedrig, 1 laufen nicht, 1 nicht messbar", check.Measurement);
        Assert.StartsWith("Heavy: hoch", check.Details[0]);
        Assert.StartsWith("Middle: mittel", check.Details[1]);
        Assert.StartsWith("Light: niedrig", check.Details[2]);
        Assert.StartsWith("Locked: nicht messbar", check.Details[3]);
        Assert.DoesNotContain(check.Details, d => d.StartsWith("Disabled") || d.StartsWith("Gone"));
    }

    [Fact]
    public void Impact_uses_io_and_memory_as_well_as_cpu_and_sums_processes_of_one_name()
    {
        var snapshot = WithStartup(
            new[] { Startup("Syncer"), Startup("Ram") },
            Proc("Syncer", pid: 1, io: 3 * 1024 * 1024),
            Proc("Syncer", pid: 2, io: 3 * 1024 * 1024), // zusammen 6 MB/s > Schwelle "hoch"
            Proc("Ram", ws: 600L * 1024 * 1024));

        Check check = Find(snapshot, "startup.impact");

        Assert.Equal("2 hoch, 0 mittel, 0 niedrig, 0 laufen nicht, 0 nicht messbar", check.Measurement);
    }

    [Fact]
    public void Several_entries_for_one_program_count_once_and_a_quiet_system_is_unremarkable()
    {
        var snapshot = WithStartup(
            new[] { Startup("Tool"), Startup("tool") },
            Proc("Tool", cpu: 0.1, ws: 1024));

        Check check = Find(snapshot, "startup.impact");

        Assert.Equal(CheckOutcome.Ok, check.Outcome);
        Assert.Equal("0 hoch, 0 mittel, 1 niedrig, 0 laufen nicht, 0 nicht messbar", check.Measurement);
    }

    [Fact]
    public void Impact_is_unavailable_when_startup_entries_or_processes_are_missing()
    {
        Check none = Find(Empty(), "startup.impact");
        Check onlyEntries = Find(Empty() with { StartupEntries = Yes<IReadOnlyList<StartupEntry>>(new[] { Startup("A") }) }, "startup.impact");

        Assert.Equal(CheckOutcome.Unavailable, none.Outcome);
        Assert.Equal(CheckOutcome.Unavailable, onlyEntries.Outcome);
        Assert.Contains("im Test nicht gemessen", onlyEntries.Explanation);
    }

    // ------------------------------------------------------------ Treiberalter

    private static DriverInfo Driver(string name, string cls, int ageYears, string maker = "Contoso")
        => new(name, maker, cls, "1.0", DateTime.Now.AddYears(-ageYears));

    [Fact]
    public void Old_drivers_of_relevant_classes_raise_a_hint_and_microsoft_drivers_are_ignored()
    {
        var drivers = new List<DriverInfo>
        {
            Driver("Old NIC", "NET", 8),
            Driver("Old mouse", "MOUSE", 9),
            Driver("Fresh GPU", "DISPLAY", 1),
            Driver("Inbox thing", "SYSTEM", 15, maker: "Microsoft"),
            Driver("Standard thing", "SYSTEM", 15, maker: "(Standard system devices)"),
        };

        Check check = Find(Empty() with { Drivers = Yes<IReadOnlyList<DriverInfo>>(drivers) }, "sys.drivers");

        Assert.Equal(CheckOutcome.Info, check.Outcome);
        Assert.Contains("2 von 3", check.Measurement);
        Assert.Contains("1 bei Grafik, Netzwerk", check.Measurement);
        Assert.StartsWith("Old mouse", check.Details[0]); // ältester zuerst
        Assert.DoesNotContain(check.Details, d => d.Contains("Inbox") || d.Contains("Standard"));
    }

    [Fact]
    public void Old_drivers_of_other_classes_alone_do_not_raise_a_hint()
    {
        var drivers = new List<DriverInfo> { Driver("Old mouse", "MOUSE", 9) };

        Check check = Find(Empty() with { Drivers = Yes<IReadOnlyList<DriverInfo>>(drivers) }, "sys.drivers");

        Assert.Equal(CheckOutcome.Ok, check.Outcome);
        Assert.Single(check.Details);
    }

    [Fact]
    public void Placeholder_and_windows_supplied_dates_say_nothing_about_age_and_are_not_counted()
    {
        var drivers = new List<DriverInfo>
        {
            new("Intel SMBus", "Intel", "SYSTEM", "10.1.57.5", new DateTime(1968, 7, 18)),          // Platzhalterdatum
            new("Print Queue", "Canon", "PRINTQUEUE", "10.0.26100.1", new DateTime(2006, 6, 21)),    // Windows-Standarddatum
            new("Processor", "Intel", "PROCESSOR", "10.0.26100.9549", new DateTime(2009, 4, 21)),    // Windows-Versionsnummer
            Driver("Real old NIC", "NET", 7),
        };

        Check check = Find(Empty() with { Drivers = Yes<IReadOnlyList<DriverInfo>>(drivers) }, "sys.drivers");

        Assert.Contains("1 von 1", check.Measurement);
        Assert.Contains("3 mit Standard- oder Platzhalterdatum nicht gewertet", check.Measurement);
        Assert.Single(check.Details);
        Assert.StartsWith("Real old NIC", check.Details[0]);
    }

    [Fact]
    public void Drivers_without_a_date_are_not_counted_and_unavailable_drivers_say_why()
    {
        var undated = new DriverInfo("No date", "Contoso", "NET", "1", null);

        Check counted = Find(Empty() with { Drivers = Yes<IReadOnlyList<DriverInfo>>(new[] { undated }) }, "sys.drivers");
        Check unavailable = Find(Empty(), "sys.drivers");

        Assert.Contains("0 von 0", counted.Measurement);
        Assert.Equal(CheckOutcome.Unavailable, unavailable.Outcome);
    }

    // ------------------------------------------------------------ Gesamt

    [Fact]
    public void Every_check_id_stays_unique_with_the_new_checks()
    {
        IReadOnlyList<Check> checks = SlownessRules.Evaluate(Empty());

        Assert.Equal(checks.Count, checks.Select(c => c.Id).Distinct().Count());
        Assert.Contains(checks, c => c.Id == "drive.shadow");
        Assert.Contains(checks, c => c.Id == "startup.impact");
        Assert.Contains(checks, c => c.Id == "startup.shell");
        Assert.Contains(checks, c => c.Id == "startup.browserext");
        Assert.Contains(checks, c => c.Id == "sys.drivers");
    }
}
