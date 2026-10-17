using System.ComponentModel;
using System.Diagnostics;

namespace Cleanii.Core.Diagnostics;

/// <summary>
/// Stichprobe über alle Prozesse: zwei Messungen im Abstand des Fensters, daraus Prozessorlast und Festplatten-Zugriffe.
/// Nicht lesbare Prozesse (meist ohne Administratorrechte) werden als solche markiert, nicht weggelassen.
/// </summary>
internal static class ProcessProbe
{
    private sealed record Raw(int Pid, string Name, long WorkingSet, TimeSpan? Cpu, ulong? IoBytes);

    public static async Task<ProcessSampleResult> SampleAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Das Messfenster muss positiv sein.");
        }

        Dictionary<int, Raw> first = Snapshot();
        (ulong idle0, ulong kernel0, ulong user0) = ReadSystemTimes();
        long start = Stopwatch.GetTimestamp();

        await Task.Delay(window, cancellationToken).ConfigureAwait(false);

        Dictionary<int, Raw> second = Snapshot();
        (ulong idle1, ulong kernel1, ulong user1) = ReadSystemTimes();
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;

        ulong total = (kernel1 - kernel0) + (user1 - user0);
        ulong idle = idle1 - idle0;
        double systemCpu = total == 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);

        int cores = Environment.ProcessorCount;
        var samples = new List<ProcessSample>(second.Count);
        foreach (Raw after in second.Values)
        {
            first.TryGetValue(after.Pid, out Raw? before);
            bool sameProcess = before is not null && string.Equals(before.Name, after.Name, StringComparison.OrdinalIgnoreCase);

            bool cpuMeasured = sameProcess && before!.Cpu is not null && after.Cpu is not null && after.Cpu >= before.Cpu;
            double cpu = cpuMeasured ? (after.Cpu!.Value - before!.Cpu!.Value).TotalSeconds / (seconds * cores) * 100 : 0;

            bool ioMeasured = sameProcess && before!.IoBytes is not null && after.IoBytes is not null && after.IoBytes >= before.IoBytes;
            double io = ioMeasured ? (after.IoBytes!.Value - before!.IoBytes!.Value) / seconds : 0;

            samples.Add(new ProcessSample(after.Pid, after.Name, cpu, after.WorkingSet, io, cpuMeasured, ioMeasured));
        }

        return new ProcessSampleResult(TimeSpan.FromSeconds(seconds), systemCpu, samples);
    }

    private static Dictionary<int, Raw> Snapshot()
    {
        var result = new Dictionary<int, Raw>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                if (pid == 0 || pid == Environment.ProcessId)
                {
                    continue; // Leerlauf-Pseudoprozess und das Programm selbst
                }

                try
                {
                    result[pid] = new Raw(pid, process.ProcessName, process.WorkingSet64, TryCpu(process), TryIo(pid));
                }
                catch (InvalidOperationException)
                {
                    // Der Prozess ist zwischen Auflisten und Lesen beendet worden.
                }
            }
        }

        return result;
    }

    private static TimeSpan? TryCpu(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch (Win32Exception)
        {
            return null; // Zugriff verweigert
        }
        catch (InvalidOperationException)
        {
            return null; // beendet
        }
    }

    private static ulong? TryIo(int pid)
    {
        IntPtr handle = NativeDiagnostics.OpenProcess(NativeDiagnostics.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return NativeDiagnostics.GetProcessIoCounters(handle, out NativeDiagnostics.IoCounters counters)
                ? counters.ReadTransferCount + counters.WriteTransferCount
                : null;
        }
        finally
        {
            NativeDiagnostics.CloseHandle(handle);
        }
    }

    private static (ulong Idle, ulong Kernel, ulong User) ReadSystemTimes()
    {
        if (!NativeDiagnostics.GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Systemzeiten nicht lesbar.");
        }

        return (idle, kernel, user);
    }
}
