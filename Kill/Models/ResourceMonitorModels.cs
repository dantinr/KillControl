namespace Kill.Models;

[Flags]
public enum ResourceMetricSelection
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    Disk = 4,
    Network = 8
}

public sealed record ResourceMonitorOptions(ResourceMetricSelection Metrics, TimeSpan Interval,
    TimeSpan? Duration = null)
{
    public string MetricsValue => string.Join(',', Enum.GetValues<ResourceMetricSelection>()
        .Where(metric => metric != ResourceMetricSelection.None && Metrics.HasFlag(metric)));
}

public static class ResourceMonitorTiming
{
    public static bool HasReachedDuration(TimeSpan? duration, TimeSpan elapsed) =>
        duration is not null && elapsed >= duration.Value;

    public static TimeSpan GetWakeDelay(ResourceMonitorOptions options, TimeSpan nextSampleAt,
        TimeSpan elapsed)
    {
        var wakeAt = options.Duration is not null && options.Duration.Value < nextSampleAt
            ? options.Duration.Value
            : nextSampleAt;
        return wakeAt > elapsed ? wakeAt - elapsed : TimeSpan.Zero;
    }

    public static TimeSpan AdvanceSampleDeadline(TimeSpan currentDeadline, TimeSpan interval,
        TimeSpan elapsed)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var nextDeadline = currentDeadline + interval;
        if (nextDeadline > elapsed) return nextDeadline;

        var skippedIntervals = (elapsed.Ticks - nextDeadline.Ticks) / interval.Ticks + 1;
        return nextDeadline + TimeSpan.FromTicks(interval.Ticks * skippedIntervals);
    }
}

public sealed class ResourceSample
{
    public long Id { get; init; }
    public long SessionId { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public double SampleDurationSeconds { get; init; }
    public double? CpuPercent { get; init; }
    public long? MemoryUsedBytes { get; init; }
    public long? MemoryTotalBytes { get; init; }
    public double? MemoryPercent { get; init; }
    public double? DiskActivePercent { get; init; }
    public double? DiskReadBytesPerSecond { get; init; }
    public double? DiskWriteBytesPerSecond { get; init; }
    public double? NetworkReceivedBytesPerSecond { get; init; }
    public double? NetworkSentBytesPerSecond { get; init; }

    public string CapturedAtLabel => CapturedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string CpuLabel => FormatPercent(CpuPercent);
    public string MemoryLabel => MemoryUsedBytes is null ? "-" : FormatBytes(MemoryUsedBytes.Value);
    public string MemoryDetailLabel => MemoryUsedBytes is null || MemoryTotalBytes is null
        ? "等待采样"
        : $"{FormatBytes(MemoryUsedBytes.Value)} / {FormatBytes(MemoryTotalBytes.Value)}";
    public string MemoryPercentLabel => FormatPercent(MemoryPercent);
    public string DiskActiveLabel => FormatPercent(DiskActivePercent);
    public string DiskReadLabel => FormatRate(DiskReadBytesPerSecond);
    public string DiskWriteLabel => FormatRate(DiskWriteBytesPerSecond);
    public string NetworkReceivedLabel => FormatRate(NetworkReceivedBytesPerSecond);
    public string NetworkSentLabel => FormatRate(NetworkSentBytesPerSecond);

    public static string FormatRate(double? bytesPerSecond) => bytesPerSecond is null
        ? "-"
        : $"{FormatBytes((long)Math.Max(0, bytesPerSecond.Value))}/s";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string FormatPercent(double? value) => value is null ? "-" : $"{value.Value:0.0}%";
}
