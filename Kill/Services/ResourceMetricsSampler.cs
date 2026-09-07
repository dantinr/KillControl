using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Kill.Models;

namespace Kill.Services;

public sealed class ResourceMetricsSampler : IDisposable
{
    private readonly ResourceMetricSelection _metrics;
    private readonly DiskPerformanceQuery? _diskQuery;
    private CpuTimes? _previousCpuTimes;
    private NetworkTotals? _previousNetworkTotals;
    private long _previousTimestamp;

    public ResourceMetricsSampler(ResourceMetricSelection metrics)
    {
        _metrics = metrics;
        _diskQuery = metrics.HasFlag(ResourceMetricSelection.Disk) ? DiskPerformanceQuery.TryCreate() : null;
        ResetBaseline();
    }

    public void ResetBaseline()
    {
        _previousCpuTimes = _metrics.HasFlag(ResourceMetricSelection.Cpu) ? TryReadCpuTimes() : null;
        _previousNetworkTotals = _metrics.HasFlag(ResourceMetricSelection.Network) ? TryReadNetworkTotals() : null;
        _previousTimestamp = Stopwatch.GetTimestamp();
        _diskQuery?.Prime();
    }

    public ResourceSample Capture(long sessionId)
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        var elapsedSeconds = Math.Max(0.001, (nowTimestamp - _previousTimestamp) / (double)Stopwatch.Frequency);
        _previousTimestamp = nowTimestamp;

        var cpuPercent = _metrics.HasFlag(ResourceMetricSelection.Cpu) ? ReadCpuPercent() : null;
        var memory = _metrics.HasFlag(ResourceMetricSelection.Memory) ? TryReadMemory() : null;
        var disk = _metrics.HasFlag(ResourceMetricSelection.Disk) ? _diskQuery?.Read() : null;
        var network = _metrics.HasFlag(ResourceMetricSelection.Network) ? ReadNetworkRates(elapsedSeconds) : null;

        return new ResourceSample
        {
            SessionId = sessionId,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            SampleDurationSeconds = elapsedSeconds,
            CpuPercent = cpuPercent,
            MemoryUsedBytes = memory?.UsedBytes,
            MemoryTotalBytes = memory?.TotalBytes,
            MemoryPercent = memory?.Percent,
            DiskActivePercent = disk?.ActivePercent,
            DiskReadBytesPerSecond = disk?.ReadBytesPerSecond,
            DiskWriteBytesPerSecond = disk?.WriteBytesPerSecond,
            NetworkReceivedBytesPerSecond = network?.ReceivedBytesPerSecond,
            NetworkSentBytesPerSecond = network?.SentBytesPerSecond
        };
    }

    public void Dispose() => _diskQuery?.Dispose();

    private double? ReadCpuPercent()
    {
        var current = TryReadCpuTimes();
        var previous = _previousCpuTimes;
        _previousCpuTimes = current;
        if (current is null || previous is null) return null;

        var idle = current.Value.Idle - previous.Value.Idle;
        var total = current.Value.Kernel - previous.Value.Kernel + current.Value.User - previous.Value.User;
        if (total == 0 || idle > total) return null;
        return Math.Clamp((total - idle) * 100d / total, 0, 100);
    }

    private NetworkRates? ReadNetworkRates(double elapsedSeconds)
    {
        var current = TryReadNetworkTotals();
        var previous = _previousNetworkTotals;
        _previousNetworkTotals = current;
        if (current is null || previous is null) return null;

        var received = current.Value.ReceivedBytes - previous.Value.ReceivedBytes;
        var sent = current.Value.SentBytes - previous.Value.SentBytes;
        if (received < 0 || sent < 0) return null;
        return new NetworkRates(received / elapsedSeconds, sent / elapsedSeconds);
    }

    private static CpuTimes? TryReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        return new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static MemorySnapshot? TryReadMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status)) return null;
        var used = status.TotalPhysical - status.AvailablePhysical;
        return new MemorySnapshot((long)used, (long)status.TotalPhysical, status.MemoryLoad);
    }

    private static NetworkTotals? TryReadNetworkTotals()
    {
        try
        {
            long received = 0;
            long sent = 0;
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var statistics = networkInterface.GetIPStatistics();
                received += Math.Max(0, statistics.BytesReceived);
                sent += Math.Max(0, statistics.BytesSent);
            }
            return new NetworkTotals(received, sent);
        }
        catch
        {
            return null;
        }
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
    private readonly record struct MemorySnapshot(long UsedBytes, long TotalBytes, double Percent);
    private readonly record struct NetworkTotals(long ReceivedBytes, long SentBytes);
    private readonly record struct NetworkRates(double ReceivedBytesPerSecond, double SentBytesPerSecond);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed class DiskPerformanceQuery : IDisposable
    {
        private const uint FormatDouble = 0x00000200;
        private readonly nint _query;
        private readonly nint _activeCounter;
        private readonly nint _readCounter;
        private readonly nint _writeCounter;
        private bool _disposed;

        private DiskPerformanceQuery(nint query, nint activeCounter, nint readCounter, nint writeCounter)
        {
            _query = query;
            _activeCounter = activeCounter;
            _readCounter = readCounter;
            _writeCounter = writeCounter;
        }

        public static DiskPerformanceQuery? TryCreate()
        {
            if (PdhOpenQuery(null, 0, out var query) != 0) return null;
            var active = AddCounter(query, @"\PhysicalDisk(_Total)\% Disk Time");
            var read = AddCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            var write = AddCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            if (active == 0 && read == 0 && write == 0)
            {
                PdhCloseQuery(query);
                return null;
            }
            return new DiskPerformanceQuery(query, active, read, write);
        }

        public void Prime() => PdhCollectQueryData(_query);

        public DiskSnapshot? Read()
        {
            if (_disposed || PdhCollectQueryData(_query) != 0) return null;
            var active = ReadCounter(_activeCounter);
            var read = ReadCounter(_readCounter);
            var write = ReadCounter(_writeCounter);
            if (active is null && read is null && write is null) return null;
            return new DiskSnapshot(active is null ? null : Math.Max(0, active.Value),
                read is null ? null : Math.Max(0, read.Value), write is null ? null : Math.Max(0, write.Value));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PdhCloseQuery(_query);
        }

        private static nint AddCounter(nint query, string path) =>
            PdhAddEnglishCounter(query, path, 0, out var counter) == 0 ? counter : 0;

        private static double? ReadCounter(nint counter)
        {
            if (counter == 0 || PdhGetFormattedCounterValue(counter, FormatDouble, out _, out var value) != 0)
                return null;
            return value.Status is 0 or 1 && double.IsFinite(value.DoubleValue) ? value.DoubleValue : null;
        }

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint counterType,
            out PdhFormattedCounterValue value);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(nint query);

        [StructLayout(LayoutKind.Explicit)]
        private struct PdhFormattedCounterValue
        {
            [FieldOffset(0)] public uint Status;
            [FieldOffset(8)] public double DoubleValue;
        }
    }

    private sealed record DiskSnapshot(double? ActivePercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond);
}
