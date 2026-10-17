using Cleanii.Core.Diagnostics;

namespace Cleanii.Tests;

/// <summary>Läuft gegen das echte System (nur lesend). Prüft, dass die Messungen funktionieren, nicht bestimmte Werte.</summary>
public class SlownessAnalyzerSmokeTests
{
    private static async Task<SlownessReport> RunShort()
        => await SlownessAnalyzer.RunAsync(new SlownessOptions { SampleWindow = TimeSpan.FromSeconds(2) });

    [Fact]
    public async Task A_real_run_returns_checks_and_measures_the_core_values()
    {
        SlownessReport report = await RunShort();
        SlownessSnapshot snapshot = report.Snapshot;

        Assert.NotEmpty(report.Checks);
        Assert.Equal(report.Checks.Count, report.Checks.Select(c => c.Id).Distinct().Count());

        Assert.True(snapshot.Volumes.IsAvailable, snapshot.Volumes.Reason);
        Assert.Contains(snapshot.Volumes.Value, v => v.IsSystem);

        Assert.True(snapshot.Memory.IsAvailable, snapshot.Memory.Reason);
        Assert.InRange(snapshot.Memory.Value.AvailablePercent, 0.01, 100);

        Assert.True(snapshot.Processes.IsAvailable, snapshot.Processes.Reason);
        Assert.InRange(snapshot.Processes.Value.SystemCpuPercent, 0, 100);
        Assert.Contains(snapshot.Processes.Value.Processes, p => p.CpuMeasured);
        Assert.DoesNotContain(snapshot.Processes.Value.Processes, p => p.Pid == Environment.ProcessId);

        Assert.True(snapshot.Config.IsAvailable, snapshot.Config.Reason);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Config.Value.PowerPlanName));
        Assert.True(snapshot.Config.Value.Uptime > TimeSpan.Zero);

        Assert.True(snapshot.StartupEntries.IsAvailable, snapshot.StartupEntries.Reason);
        Assert.True(snapshot.Services.IsAvailable, snapshot.Services.Reason);
    }

    [Fact]
    public async Task Every_unavailable_measurement_has_a_reason_and_no_unexpected_failure_escapes()
    {
        SlownessReport report = await RunShort();

        foreach (Check check in report.Checks.Where(c => c.Outcome == CheckOutcome.Unavailable))
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Explanation), $"{check.Id} ohne Grund");
        }
    }

    [Fact]
    public async Task A_cancelled_run_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SlownessAnalyzer.RunAsync(new SlownessOptions { SampleWindow = TimeSpan.FromSeconds(2) }, cancellationToken: cts.Token));
    }
}
