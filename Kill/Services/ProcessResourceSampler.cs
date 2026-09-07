using System.Diagnostics;
using System.Runtime.InteropServices;
using Kill.Models;

namespace Kill.Services;

public sealed class ProcessResourceSampler
{
    private readonly object _sync = new();
    private Dictionary<int, ProcessBaseline> _previousProcesses = [];
    private long _previousTimestamp;

    public ProcessResourceSampler() => ResetBaseline();

    public void ResetBaseline()
    {
        lock (_sync)
        {
            _previousProcesses = ReadProcesses().Baselines;
            _previousTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public ProcessResourceSnapshot Capture()
    {
        lock (_sync)
        {
            var nowTimestamp = Stopwatch.GetTimestamp();
            var elapsedSeconds = Math.Max(0.001,
                (nowTimestamp - _previousTimestamp) / (double)Stopwatch.Frequency);
            var capturedAt = DateTimeOffset.UtcNow;
            var current = ReadProcesses();
            var samples = new List<ProcessResourceSample>(current.Processes.Count);

            foreach (var process in current.Processes)
            {
                _previousProcesses.TryGetValue(process.ProcessId, out var previous);
                var isSameProcess = previous is not null && IsSameProcess(previous, process.Baseline);

                samples.Add(new ProcessResourceSample
                {
                    ProcessId = process.ProcessId,
                    ProcessName = process.ProcessName,
                    CapturedAtUtc = capturedAt,
                    SampleDurationSeconds = elapsedSeconds,
                    CpuPercent = isSameProcess
                        ? CalculateCpuPercent(previous!.TotalProcessorTicks, process.Baseline.TotalProcessorTicks,
                            elapsedSeconds)
                        : null,
                    WorkingSetBytes = process.WorkingSetBytes,
                    IoReadBytesPerSecond = isSameProcess
                        ? CalculateRate(previous!.ReadTransferBytes, process.Baseline.ReadTransferBytes, elapsedSeconds)
                        : null,
                    IoWriteBytesPerSecond = isSameProcess
                        ? CalculateRate(previous!.WriteTransferBytes, process.Baseline.WriteTransferBytes, elapsedSeconds)
                        : null,
                    IoOtherBytesPerSecond = isSameProcess
                        ? CalculateRate(previous!.OtherTransferBytes, process.Baseline.OtherTransferBytes, elapsedSeconds)
                        : null
                });
            }

            _previousProcesses = current.Baselines;
            _previousTimestamp = nowTimestamp;
            return new ProcessResourceSnapshot(capturedAt, elapsedSeconds, samples);
        }
    }

    public IReadOnlyList<ProcessResourceSample> CaptureTop(int limit, ProcessResourceSort sort) =>
        Capture().GetTop(limit, sort);

    private static ProcessReadResult ReadProcesses()
    {
        var baselines = new Dictionary<int, ProcessBaseline>();
        var processes = new List<CurrentProcess>();
        Process[] runningProcesses;

        try
        {
            runningProcesses = Process.GetProcesses();
        }
        catch
        {
            return new ProcessReadResult(baselines, processes);
        }

        foreach (var process in runningProcesses)
        {
            using (process)
            {
                var current = TryReadProcess(process);
                if (current is null) continue;

                processes.Add(current);
                baselines[current.ProcessId] = current.Baseline;
            }
        }

        return new ProcessReadResult(baselines, processes);
    }

    private static CurrentProcess? TryReadProcess(Process process)
    {
        int processId;
        try
        {
            processId = process.Id;
        }
        catch
        {
            return null;
        }

        var processName = TryRead(() => process.ProcessName) ?? $"PID {processId}";
        var startTimeUtcTicks = TryRead(() => process.StartTime.ToUniversalTime().Ticks);
        var totalProcessorTicks = TryRead(() => process.TotalProcessorTime.Ticks);
        var workingSetBytes = TryRead(() => Math.Max(0, process.WorkingSet64));
        var io = TryReadIoCounters(process);
        var baseline = new ProcessBaseline(startTimeUtcTicks, totalProcessorTicks,
            io?.ReadTransferCount, io?.WriteTransferCount, io?.OtherTransferCount);

        return new CurrentProcess(processId, processName, workingSetBytes, baseline);
    }

    private static bool IsSameProcess(ProcessBaseline previous, ProcessBaseline current)
    {
        if (previous.StartTimeUtcTicks is not null && current.StartTimeUtcTicks is not null)
            return previous.StartTimeUtcTicks == current.StartTimeUtcTicks;

        var comparedCounter = false;
        if (!IsMonotonic(previous.TotalProcessorTicks, current.TotalProcessorTicks, ref comparedCounter) ||
            !IsMonotonic(previous.ReadTransferBytes, current.ReadTransferBytes, ref comparedCounter) ||
            !IsMonotonic(previous.WriteTransferBytes, current.WriteTransferBytes, ref comparedCounter) ||
            !IsMonotonic(previous.OtherTransferBytes, current.OtherTransferBytes, ref comparedCounter))
            return false;

        return comparedCounter;
    }

    private static bool IsMonotonic(long? previous, long? current, ref bool comparedCounter)
    {
        if (previous is null || current is null) return true;
        comparedCounter = true;
        return current >= previous;
    }

    private static bool IsMonotonic(ulong? previous, ulong? current, ref bool comparedCounter)
    {
        if (previous is null || current is null) return true;
        comparedCounter = true;
        return current >= previous;
    }

    private static double? CalculateCpuPercent(long? previousTicks, long? currentTicks, double elapsedSeconds)
    {
        if (previousTicks is null || currentTicks is null || currentTicks < previousTicks) return null;

        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var busySeconds = TimeSpan.FromTicks(currentTicks.Value - previousTicks.Value).TotalSeconds;
        return Math.Clamp(busySeconds * 100 / (elapsedSeconds * processorCount), 0, 100);
    }

    private static double? CalculateRate(ulong? previousBytes, ulong? currentBytes, double elapsedSeconds)
    {
        if (previousBytes is null || currentBytes is null || currentBytes < previousBytes) return null;
        return (currentBytes.Value - previousBytes.Value) / elapsedSeconds;
    }

    private static IoCounters? TryReadIoCounters(Process process)
    {
        try
        {
            return GetProcessIoCounters(process.Handle, out var counters) ? counters : null;
        }
        catch
        {
            return null;
        }
    }

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint processHandle, out IoCounters ioCounters);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoCounters
    {
        public readonly ulong ReadOperationCount;
        public readonly ulong WriteOperationCount;
        public readonly ulong OtherOperationCount;
        public readonly ulong ReadTransferCount;
        public readonly ulong WriteTransferCount;
        public readonly ulong OtherTransferCount;
    }

    private sealed record ProcessBaseline(long? StartTimeUtcTicks, long? TotalProcessorTicks,
        ulong? ReadTransferBytes, ulong? WriteTransferBytes, ulong? OtherTransferBytes);

    private sealed record CurrentProcess(int ProcessId, string ProcessName, long? WorkingSetBytes,
        ProcessBaseline Baseline);

    private sealed record ProcessReadResult(Dictionary<int, ProcessBaseline> Baselines,
        List<CurrentProcess> Processes);
}
