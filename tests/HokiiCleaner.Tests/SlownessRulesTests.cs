using HokiiCleaner.Core.Diagnostics;

namespace HokiiCleaner.Tests;

public class SlownessRulesTests
{
    private static Measured<T> Unavailable<T>() => Measured<T>.Unavailable("im Test nicht gemessen");

    private static SlownessSnapshot Empty(bool elevated = false) => new(
        elevated,
        Unavailable<IReadOnlyList<VolumeInfo>>(),
        Unavailable<IReadOnlyList<PhysicalDiskInfo>>(),
        Unavailable<DiskActivity>(),
        Unavailable<IReadOnlyList<StartupEntry>>(),
        Unavailable<IReadOnlyList<ScheduledTaskEntry>>(),
        Unavailable<IReadOnlyList<ServiceEntry>>(),
        Unavailable<ProcessSampleResult>(),
        Unavailable<MemoryInfo>(),
        Unavailable<SystemConfig>(),
        Unavailable<IReadOnlyList<DisplayDriverInfo>>(),
        Unavailable<IReadOnlyList<ShellExtensionInfo>>(),
        Unavailable<IReadOnlyList<BrowserExtensionInfo>>(),
        Unavailable<IReadOnlyList<DriverInfo>>(),
        Unavailable<ShadowStorageInfo>());

    private static Check Find(IEnumerable<Check> checks, string id) => Assert.Single(checks, c => c.Id == id);

    private static Measured<IReadOnlyList<VolumeInfo>> Volumes(params VolumeInfo[] volumes) => Measured<IReadOnlyList<VolumeInfo>>.Of(volumes);

    private static VolumeInfo Volume(double freePercent, bool system = true, string name = @"C:\")
        => new(name, null, 1000, (long)(freePercent * 10), system);

    private static Measured<MemoryInfo> Memory(double availablePercent = 50, double commitPercent = 30)
        => Measured<MemoryInfo>.Of(new MemoryInfo(1000, (long)(availablePercent * 10), (long)(commitPercent * 10), 1000, null, null));

    private static SystemConfig Config(
        string plan = "Ausbalanciert",
        Guid? planId = null,
        TimeSpan? uptime = null,
        bool? fast = false,
        bool update = false,
        bool servicing = false,
        bool rename = false)
        => new(plan, planId ?? KnownPowerPlans.Balanced, uptime ?? TimeSpan.FromHours(3), fast, update, servicing, rename);

    private static Measured<ProcessSampleResult> Processes(double systemCpu, params ProcessSample[] samples)
        => Measured<ProcessSampleResult>.Of(new ProcessSampleResult(TimeSpan.FromSeconds(5), systemCpu, samples));

    private static ProcessSample Proc(string name, double cpu = 0, long ws = 0, double io = 0, bool measured = true, int pid = 1)
        => new(pid, name, cpu, ws, io, measured, measured);

    // ------------------------------------------------------------ Laufwerk

    [Theory]
    [InlineData(20, CheckOutcome.Ok)]
    [InlineData(15, CheckOutcome.Ok)]
    [InlineData(14, CheckOutcome.Warning)]
    [InlineData(10, CheckOutcome.Warning)]
    [InlineData(9, CheckOutcome.Critical)]
    public void Free_space_follows_the_thresholds(double freePercent, CheckOutcome expected)
    {
        var snapshot = Empty() with { Volumes = Volumes(Volume(freePercent)) };

        Check check = Find(SlownessRules.Evaluate(snapshot), @"drive.free.C:\");

        Assert.Equal(expected, check.Outcome);
        Assert.NotNull(check.Threshold);
    }

    [Fact]
    public void Unavailable_measurements_become_unavailable_checks_with_the_reason()
    {
        IReadOnlyList<Check> checks = SlownessRules.Evaluate(Empty());

        Check free = Find(checks, "drive.free");
        Assert.Equal(CheckOutcome.Unavailable, free.Outcome);
        Assert.Contains("im Test nicht gemessen", free.Explanation);
        Assert.DoesNotContain(checks, c => c.Outcome is CheckOutcome.Warning or CheckOutcome.Critical);
    }

    [Theory]
    [InlineData(DiskMedia.Hdd, true, CheckOutcome.Warning)]
    [InlineData(DiskMedia.Hdd, false, CheckOutcome.Ok)]
    [InlineData(DiskMedia.Ssd, true, CheckOutcome.Ok)]
    [InlineData(DiskMedia.Unknown, true, CheckOutcome.Info)]
    public void Windows_on_a_hdd_is_a_warning(DiskMedia media, bool hostsSystem, CheckOutcome expected)
    {
        var disk = new PhysicalDiskInfo("0", "Testplatte", media, "SATA", DiskHealth.Healthy, 1000,
            hostsSystem ? new[] { @"C:\" } : new[] { @"D:\" });
        var snapshot = Empty() with
        {
            Volumes = Volumes(Volume(50)),
            Disks = Measured<IReadOnlyList<PhysicalDiskInfo>>.Of(new[] { disk }),
        };

        Assert.Equal(expected, Find(SlownessRules.Evaluate(snapshot), "drive.media.0").Outcome);
    }

    [Theory]
    [InlineData(DiskHealth.Healthy, CheckOutcome.Ok)]
    [InlineData(DiskHealth.Warning, CheckOutcome.Warning)]
    [InlineData(DiskHealth.Unhealthy, CheckOutcome.Critical)]
    [InlineData(DiskHealth.Unknown, CheckOutcome.Info)]
    public void Disk_health_is_mapped_to_outcomes(DiskHealth health, CheckOutcome expected)
    {
        var disk = new PhysicalDiskInfo("1", "Testplatte", DiskMedia.Ssd, "NVMe", health, 1000, Array.Empty<string>());
        var snapshot = Empty() with { Disks = Measured<IReadOnlyList<PhysicalDiskInfo>>.Of(new[] { disk }) };

        Assert.Equal(expected, Find(SlownessRules.Evaluate(snapshot), "drive.health.1").Outcome);
    }

    [Fact]
    public void Smart_and_boot_time_are_always_reported_as_unavailable()
    {
        IReadOnlyList<Check> checks = SlownessRules.Evaluate(Empty(elevated: true));

        Assert.Equal(CheckOutcome.Unavailable, Find(checks, "drive.smart").Outcome);
        Assert.Equal(CheckOutcome.Unavailable, Find(checks, "startup.boottime").Outcome);
    }

    [Theory]
    [InlineData(2, 0.001, CheckOutcome.Ok)]
    [InlineData(85, 0.001, CheckOutcome.Warning)]
    [InlineData(5, 0.06, CheckOutcome.Warning)]
    public void Disk_activity_warns_on_sustained_load_or_slow_responses(double busy, double latency, CheckOutcome expected)
    {
        var activity = new DiskActivity(TimeSpan.FromSeconds(5), new[] { new DiskActivityInstance("0 C:", busy, 100, latency, 1) });
        var snapshot = Empty() with { DiskActivity = Measured<DiskActivity>.Of(activity) };

        Assert.Equal(expected, Find(SlownessRules.Evaluate(snapshot), "drive.activity.0 C:").Outcome);
    }

    // ------------------------------------------------------------ Autostart

    private static StartupEntry Entry(string name, bool enabled = true, bool? exists = true)
        => new(name, "cmd", "Test", enabled, @"C:\" + name + ".exe", exists);

    private static Measured<IReadOnlyList<StartupEntry>> Entries(params StartupEntry[] entries)
        => Measured<IReadOnlyList<StartupEntry>>.Of(entries);

    [Theory]
    [InlineData(10, CheckOutcome.Ok)]
    [InlineData(11, CheckOutcome.Info)]
    [InlineData(15, CheckOutcome.Info)]
    [InlineData(16, CheckOutcome.Warning)]
    public void Startup_count_follows_the_thresholds(int enabledCount, CheckOutcome expected)
    {
        StartupEntry[] entries = Enumerable.Range(0, enabledCount).Select(i => Entry("App" + i)).ToArray();
        var snapshot = Empty() with { StartupEntries = Entries(entries) };

        Check check = Find(SlownessRules.Evaluate(snapshot), "startup.count");

        Assert.Equal(expected, check.Outcome);
    }

    [Fact]
    public void Disabled_startup_entries_are_not_counted_but_reported()
    {
        StartupEntry[] entries = Enumerable.Range(0, 20).Select(i => Entry("Off" + i, enabled: false)).Append(Entry("On")).ToArray();
        var snapshot = Empty() with { StartupEntries = Entries(entries) };

        Check check = Find(SlownessRules.Evaluate(snapshot), "startup.count");

        Assert.Equal(CheckOutcome.Ok, check.Outcome);
        Assert.Contains("1 aktiv (20 deaktiviert)", check.Measurement);
        Assert.Single(check.Details);
    }

    [Fact]
    public void Broken_startup_entries_are_only_those_enabled_with_a_known_missing_file()
    {
        var snapshot = Empty() with
        {
            StartupEntries = Entries(
                Entry("Missing", exists: false),
                Entry("DisabledMissing", enabled: false, exists: false),
                Entry("Unknown", exists: null),
                Entry("Fine")),
        };

        Check check = Find(SlownessRules.Evaluate(snapshot), "startup.broken");

        Assert.Equal(CheckOutcome.Info, check.Outcome);
        Assert.Contains("1 verweisen", check.Measurement);
        Assert.Contains(check.Details, d => d.StartsWith("Missing:"));
    }

    [Fact]
    public void Scheduled_tasks_count_only_enabled_logon_or_boot_tasks_outside_the_microsoft_folder()
    {
        var tasks = new List<ScheduledTaskEntry>();
        for (int i = 0; i < 12; i++)
        {
            tasks.Add(new ScheduledTaskEntry("Own" + i, @"\Vendor\", true, i % 2 == 0, i % 2 == 1, null));
        }

        tasks.Add(new ScheduledTaskEntry("Sys", @"\Microsoft\Windows\", true, true, false, null));
        tasks.Add(new ScheduledTaskEntry("Off", @"\Vendor\", false, true, false, null));
        tasks.Add(new ScheduledTaskEntry("Daily", @"\Vendor\", true, false, false, null));
        var snapshot = Empty() with { Tasks = Measured<IReadOnlyList<ScheduledTaskEntry>>.Of(tasks) };

        Check check = Find(SlownessRules.Evaluate(snapshot), "startup.tasks");

        Assert.Equal(CheckOutcome.Info, check.Outcome);
        Assert.StartsWith("12 Aufgaben", check.Measurement);
    }

    [Fact]
    public void Services_in_the_windows_folder_are_not_counted_as_third_party()
    {
        var services = new List<ServiceEntry>();
        for (int i = 0; i < 21; i++)
        {
            services.Add(new ServiceEntry("S" + i, "Dienst " + i, $@"C:\Program Files\Vendor\s{i}.exe", true, false));
        }

        services.Add(new ServiceEntry("Win", "Windows-Dienst", @"C:\Windows\System32\svchost.exe -k x", true, false));
        var snapshot = Empty() with { Services = Measured<IReadOnlyList<ServiceEntry>>.Of(services) };

        Check check = Find(SlownessRules.Evaluate(snapshot), "startup.services");

        Assert.Equal(CheckOutcome.Info, check.Outcome);
        Assert.StartsWith("21 außerhalb", check.Measurement);
    }

    [Fact]
    public void Long_detail_lists_are_truncated_with_a_remaining_count()
    {
        StartupEntry[] entries = Enumerable.Range(0, 40).Select(i => Entry("App" + i)).ToArray();
        var snapshot = Empty() with { StartupEntries = Entries(entries) };

        Check check = Find(SlownessRules.Evaluate(snapshot), "startup.count");

        Assert.Equal(16, check.Details.Count);
        Assert.Equal("… und 25 weitere", check.Details[^1]);
    }

    // ------------------------------------------------------------ Prozesse und Speicher

    [Theory]
    [InlineData(30, CheckOutcome.Ok)]
    [InlineData(70, CheckOutcome.Warning)]
    [InlineData(89, CheckOutcome.Warning)]
    [InlineData(90, CheckOutcome.Critical)]
    public void Cpu_follows_the_thresholds_and_lists_the_top_consumers_grouped_by_name(double cpu, CheckOutcome expected)
    {
        var snapshot = Empty() with
        {
            Processes = Processes(
                cpu,
                Proc("chrome", cpu: 10, pid: 1), Proc("chrome", cpu: 15, pid: 2), Proc("other", cpu: 5, pid: 3)),
        };

        Check check = Find(SlownessRules.Evaluate(snapshot), "proc.cpu");

        Assert.Equal(expected, check.Outcome);
        Assert.StartsWith("chrome (2 Prozesse)", check.Details[0]);
        Assert.Equal(2, check.Details.Count);
    }

    [Theory]
    [InlineData(50, CheckOutcome.Ok)]
    [InlineData(20, CheckOutcome.Ok)]
    [InlineData(19, CheckOutcome.Warning)]
    [InlineData(9, CheckOutcome.Critical)]
    public void Available_memory_follows_the_thresholds(double availablePercent, CheckOutcome expected)
    {
        var snapshot = Empty() with
        {
            Memory = Memory(availablePercent),
            Processes = Processes(10, Proc("big", ws: 5_000_000_000)),
        };

        Check check = Find(SlownessRules.Evaluate(snapshot), "proc.memory");

        Assert.Equal(expected, check.Outcome);
        Assert.StartsWith("big:", check.Details[0]);
    }

    [Theory]
    [InlineData(50, CheckOutcome.Ok)]
    [InlineData(85, CheckOutcome.Warning)]
    [InlineData(95, CheckOutcome.Critical)]
    public void Commit_follows_the_thresholds(double commitPercent, CheckOutcome expected)
    {
        var snapshot = Empty() with { Memory = Memory(commitPercent: commitPercent) };

        Assert.Equal(expected, Find(SlownessRules.Evaluate(snapshot), "proc.commit").Outcome);
    }

    [Fact]
    public void A_heavy_disk_io_process_is_reported_as_info()
    {
        var snapshot = Empty() with
        {
            Processes = Processes(5, Proc("sync", io: 50 * 1024 * 1024), Proc("idle", io: 10)),
        };

        Check check = Find(SlownessRules.Evaluate(snapshot), "proc.io");

        Assert.Equal(CheckOutcome.Info, check.Outcome);
        Assert.Contains("sync", check.Measurement);
    }

    [Fact]
    public void Io_is_unavailable_when_no_process_delivered_counters()
    {
        var snapshot = Empty() with { Processes = Processes(5, Proc("a", measured: false)) };

        Assert.Equal(CheckOutcome.Unavailable, Find(SlownessRules.Evaluate(snapshot), "proc.io").Outcome);
    }

    [Fact]
    public void Low_process_coverage_is_reported_as_info()
    {
        ProcessSample[] samples = Enumerable.Range(0, 10).Select(i => Proc("p" + i, measured: i < 5, pid: i + 1)).ToArray();
        var snapshot = Empty() with { Processes = Processes(5, samples) };

        Check check = Find(SlownessRules.Evaluate(snapshot), "proc.coverage");

        Assert.Equal(CheckOutcome.Info, check.Outcome);
        Assert.Contains("5 von 10", check.Measurement);
    }

    // ------------------------------------------------------------ System

    [Fact]
    public void Power_saver_plan_is_a_warning_other_plans_are_not()
    {
        var saver = Empty() with { Config = Measured<SystemConfig>.Of(Config("Energiesparmodus", KnownPowerPlans.PowerSaver)) };
        var custom = Empty() with { Config = Measured<SystemConfig>.Of(Config("Silent", Guid.NewGuid())) };

        Assert.Equal(CheckOutcome.Warning, Find(SlownessRules.Evaluate(saver), "sys.power").Outcome);
        Assert.Equal(CheckOutcome.Ok, Find(SlownessRules.Evaluate(custom), "sys.power").Outcome);
    }

    [Theory]
    [InlineData(3, CheckOutcome.Ok)]
    [InlineData(7, CheckOutcome.Info)]
    [InlineData(13, CheckOutcome.Info)]
    [InlineData(14, CheckOutcome.Warning)]
    public void Uptime_follows_the_thresholds_and_mentions_fast_startup(double days, CheckOutcome expected)
    {
        var snapshot = Empty() with { Config = Measured<SystemConfig>.Of(Config(uptime: TimeSpan.FromDays(days), fast: true)) };

        Check check = Find(SlownessRules.Evaluate(snapshot), "sys.uptime");

        Assert.Equal(expected, check.Outcome);
        Assert.Contains("Schnellstart", check.Explanation);
    }

    [Fact]
    public void Pending_reboot_outranks_pending_file_renames()
    {
        Check None() => Find(SlownessRules.Evaluate(Empty() with { Config = Measured<SystemConfig>.Of(Config()) }), "sys.reboot");
        Check Rename() => Find(SlownessRules.Evaluate(Empty() with { Config = Measured<SystemConfig>.Of(Config(rename: true)) }), "sys.reboot");
        Check Update() => Find(SlownessRules.Evaluate(Empty() with { Config = Measured<SystemConfig>.Of(Config(update: true, rename: true)) }), "sys.reboot");
        Check Servicing() => Find(SlownessRules.Evaluate(Empty() with { Config = Measured<SystemConfig>.Of(Config(servicing: true)) }), "sys.reboot");

        Assert.Equal(CheckOutcome.Ok, None().Outcome);
        Assert.Equal(CheckOutcome.Info, Rename().Outcome);
        Assert.Equal(CheckOutcome.Warning, Update().Outcome);
        Assert.Equal(CheckOutcome.Warning, Servicing().Outcome);
    }

    [Fact]
    public void Display_driver_rules()
    {
        static IReadOnlyList<Check> Run(params DisplayDriverInfo[] drivers)
            => SlownessRules.Evaluate(Empty() with { DisplayDrivers = Measured<IReadOnlyList<DisplayDriverInfo>>.Of(drivers) });

        var basic = Run(new DisplayDriverInfo("Microsoft Basic Display Adapter", "Microsoft", DateTime.Now.AddDays(-30), "10.0"));
        var old = Run(new DisplayDriverInfo("GPU X", "Hersteller", DateTime.Now.AddDays(-1000), "1.0"));
        var fresh = Run(new DisplayDriverInfo("GPU X", "Hersteller", DateTime.Now.AddDays(-30), "1.0"));
        var none = Run();

        Assert.Equal(CheckOutcome.Warning, Assert.Single(basic, c => c.Id.StartsWith("sys.gpu")).Outcome);
        Assert.Equal(CheckOutcome.Info, Assert.Single(old, c => c.Id.StartsWith("sys.gpu")).Outcome);
        Assert.Equal(CheckOutcome.Ok, Assert.Single(fresh, c => c.Id.StartsWith("sys.gpu")).Outcome);
        Assert.Equal(CheckOutcome.Info, Assert.Single(none, c => c.Id == "sys.gpu").Outcome);
    }

    [Fact]
    public void Display_rank_orders_critical_before_warning_before_info_before_unavailable_before_ok()
    {
        Check Make(CheckOutcome outcome) => new("x", CheckArea.System, outcome, "t", "m", null, "e", "s", Array.Empty<string>());
        CheckOutcome[] shuffled = { CheckOutcome.Ok, CheckOutcome.Unavailable, CheckOutcome.Critical, CheckOutcome.Info, CheckOutcome.Warning };

        CheckOutcome[] ordered = shuffled.Select(Make).OrderBy(c => c.DisplayRank).Select(c => c.Outcome).ToArray();

        Assert.Equal(
            new[] { CheckOutcome.Critical, CheckOutcome.Warning, CheckOutcome.Info, CheckOutcome.Unavailable, CheckOutcome.Ok },
            ordered);
    }

    [Fact]
    public void Measured_value_access_on_an_unavailable_measurement_throws_with_the_reason()
    {
        Measured<int> missing = Measured<int>.Unavailable("kein Zugriff");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => missing.Value);
        Assert.Contains("kein Zugriff", ex.Message);
        Assert.False(missing.IsAvailable);
        Assert.Equal(7, Measured<int>.Of(7).Value);
    }
}
