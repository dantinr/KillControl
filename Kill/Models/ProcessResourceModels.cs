namespace Kill.Models;

public enum ProcessResourceSort
{
    Cpu,
    Memory,
    IoTotal,
    IoRead,
    IoWrite,
    IoOther
}

public sealed record ProcessResourceSample
{
    public int Rank { get; init; }
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public double SampleDurationSeconds { get; init; }
    public double? CpuPercent { get; init; }
    public long? WorkingSetBytes { get; init; }
    public double? IoReadBytesPerSecond { get; init; }
    public double? IoWriteBytesPerSecond { get; init; }
    public double? IoOtherBytesPerSecond { get; init; }

    public string DisplayName => $"{ProcessName} ({ProcessId})";
    public string CpuLabel => CpuPercent is null ? "-" : $"{CpuPercent.Value:0.0}%";
    public string MemoryLabel => WorkingSetBytes is null ? "-" : ResourceSample.FormatBytes(WorkingSetBytes.Value);
    public string IoReadLabel => ResourceSample.FormatRate(IoReadBytesPerSecond);
    public string IoWriteLabel => ResourceSample.FormatRate(IoWriteBytesPerSecond);
    public string IoOtherLabel => ResourceSample.FormatRate(IoOtherBytesPerSecond);
    public string IoTotalLabel => ResourceSample.FormatRate(IoTotalBytesPerSecond);

    public double? IoTotalBytesPerSecond => IoReadBytesPerSecond is null && IoWriteBytesPerSecond is null &&
                                                    IoOtherBytesPerSecond is null
        ? null
        : (IoReadBytesPerSecond ?? 0) + (IoWriteBytesPerSecond ?? 0) + (IoOtherBytesPerSecond ?? 0);

    public double? GetSortValue(ProcessResourceSort sort) => sort switch
    {
        ProcessResourceSort.Cpu => CpuPercent,
        ProcessResourceSort.Memory => WorkingSetBytes,
        ProcessResourceSort.IoTotal => IoTotalBytesPerSecond,
        ProcessResourceSort.IoRead => IoReadBytesPerSecond,
        ProcessResourceSort.IoWrite => IoWriteBytesPerSecond,
        ProcessResourceSort.IoOther => IoOtherBytesPerSecond,
        _ => null
    };
}

public sealed record ProcessResourceSnapshot(DateTimeOffset CapturedAtUtc, double SampleDurationSeconds,
    IReadOnlyList<ProcessResourceSample> Processes)
{
    public IReadOnlyList<ProcessResourceSample> GetTop(int limit, ProcessResourceSort sort)
    {
        if (limit <= 0) return [];

        return Processes
            .Where(process => process.GetSortValue(sort) is not null)
            .OrderByDescending(process => process.GetSortValue(sort))
            .ThenBy(process => process.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(process => process.ProcessId)
            .Take(limit)
            .Select((process, index) => process with { Rank = index + 1 })
            .ToArray();
    }
}
