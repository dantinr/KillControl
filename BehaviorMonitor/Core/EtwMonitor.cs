using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace ProcessSentinel.Core;

public sealed class EtwMonitor : IDisposable
{
    private TraceEventSession? session;
    private ProcessTracker? tracker;
    private long sequence;
    private int stopping;
    private long cachedLoss;
    private readonly RegistryResolver registry = new();
    public event Action<Activity>? ActivityReceived;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public int ActiveProcesses => tracker?.Count ?? 0;
    public ProcessSnapshot? GetProcessSnapshot(long afterRevision = -1) => tracker?.GetSnapshot(afterRevision);
    public long EventsLost
    {
        get
        {
            try { Interlocked.Exchange(ref cachedLoss, Math.Max(Interlocked.Read(ref cachedLoss), session?.EventsLost ?? 0)); } catch { }
            return Interlocked.Read(ref cachedLoss);
        }
    }
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Start(MonitorRequest request)
    {
        if (session is not null) throw new InvalidOperationException("采集器已经启动。");
        if (!IsAdministrator()) throw new UnauthorizedAccessException("ETW 内核采集需要管理员权限。");
        var snapshot = ProcessCatalog.Snapshot();
        var current = snapshot.FirstOrDefault(x => x.Id == request.Root.Id)
            ?? throw new InvalidOperationException("程序主进程已退出，请刷新后重新选择。");
        if (request.Root.StartTimeUtcTicks == 0 || current.StartTimeUtcTicks != request.Root.StartTimeUtcTicks)
            throw new InvalidOperationException("目标已退出或 PID 已被重新使用，请重新选择进程。");
        ProcessFamily? family = null;
        if (request.SelectedProcess is { } selected)
        {
            if (!request.IncludeChildren) throw new InvalidOperationException("程序进程树监控需要包含全部子进程。");
            family = ProcessFamilyResolver.Resolve(selected, snapshot);
            if (family.Root.Id != current.Id || family.Root.StartTimeUtcTicks != current.StartTimeUtcTicks)
                throw new InvalidOperationException("程序进程树已变化，请刷新后重新选择；不会自动扩大到其他程序。");
        }
        tracker = new(current, request.IncludeChildren, family?.Selected, family?.Lineage);
        try
        {
            // A private, uniquely named Windows 8+ system-logger session: never replace NT Kernel Logger.
            session = new TraceEventSession($"ProcessSentinel-{Environment.ProcessId}-{Guid.NewGuid():N}")
            { StopOnDispose = true, BufferSizeMB = 64 };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread
                | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit
                | KernelTraceEventParser.Keywords.DiskFileIO | KernelTraceEventParser.Keywords.NetworkTCPIP
                | KernelTraceEventParser.Keywords.Registry | KernelTraceEventParser.Keywords.ImageLoad);
            Subscribe(session.Source.Kernel); // Source starts a session; the kernel provider must be enabled first.
            tracker.Seed(ProcessCatalog.Snapshot());
            Completion = Task.Factory.StartNew(() => session.Source.Process(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Emit(new Activity
            {
                ProcessId = current.Id,
                ProcessName = current.Name,
                Kind = ActivityKind.System,
                Operation = "开始",
                Target = current.Path,
                Detail = (family is null ? "" : $"{family.ScopeText}\n父进程追溯边界：{family.Boundary}\n")
                    + "只记录开始监控后的事件；已存在的网络连接不属于新连接事件。"
            });
        }
        catch { Dispose(); throw; }
    }

    private void Subscribe(KernelTraceEventParser kernel)
    {
        kernel.ProcessStart += data =>
        {
            if (tracker!.Find(data.ParentID) is null && tracker.Find(data.ProcessID) is null) return;
            string path = data.ImageFileName;
            var childInfo = ProcessCatalog.ReadDetails(new(data.ProcessID, data.ParentID, Path.GetFileName(path), path, data.TimeStamp.ToUniversalTime().Ticks));
            if (tracker.Start(childInfo)) Record(data, ActivityKind.Process, "启动", childInfo.Path, $"父进程 PID {data.ParentID}\n命令行：{data.CommandLine}");
        };
        kernel.ProcessStop += data =>
        {
            Record(data, ActivityKind.Process, "退出", "", $"退出码：0x{data.ExitStatus:X8}；其存活子进程仍会被跟踪。");
            tracker!.Stop(data.ProcessID);
        };
        kernel.FileIOCreate += Tracked<FileIOCreateTraceData>(data => Record(data, ActivityKind.File, "打开/创建", data.FileName,
            $"CreateOptions：0x{data.CreateOptions:X}；ETW 请求事件，未确认完成状态。"));
        kernel.FileIORead += Tracked<FileIOReadWriteTraceData>(data => Record(data, ActivityKind.File, "读取", data.FileName, $"请求读取 {data.IoSize:N0} 字节；未确认完成状态。", data.IoSize));
        kernel.FileIOWrite += Tracked<FileIOReadWriteTraceData>(data => Record(data, ActivityKind.File, "写入", data.FileName, $"请求写入 {data.IoSize:N0} 字节；未确认完成状态。", data.IoSize));
        kernel.FileIODelete += Tracked<FileIOInfoTraceData>(data => Record(data, ActivityKind.File, "删除", data.FileName, "删除请求；未确认完成状态。"));
        kernel.FileIORename += Tracked<FileIOInfoTraceData>(data => Record(data, ActivityKind.File, "重命名", data.FileName, "重命名请求；此事件仅提供原路径，未确认完成状态。"));
        kernel.FileIOQueryInfo += Tracked<FileIOInfoTraceData>(data => Record(data, ActivityKind.File, "查询属性", data.FileName, "查询文件属性。"));
        kernel.FileIODirEnum += Tracked<FileIODirEnumTraceData>(data => Record(data, ActivityKind.File, "枚举目录", data.DirectoryName, $"匹配名称：{data.FileName}"));
        kernel.FileIOClose += Tracked<FileIOSimpleOpTraceData>(data => Record(data, ActivityKind.File, "关闭", data.FileName, "关闭文件句柄。"));
        kernel.FileIOFlush += Tracked<FileIOSimpleOpTraceData>(data => Record(data, ActivityKind.File, "刷新", data.FileName, "刷新文件缓冲区请求。"));

        kernel.RegistryKCBCreate += registry.Observe;
        kernel.RegistryKCBDelete += registry.Observe;
        kernel.RegistryKCBRundownBegin += registry.Observe;
        kernel.RegistryKCBRundownEnd += registry.Observe;
        kernel.RegistryCreate += data => Registry(data, "创建键");
        kernel.RegistryOpen += data => Registry(data, "打开键");
        kernel.RegistryQueryValue += data => Registry(data, "查询值");
        kernel.RegistrySetValue += data => Registry(data, "设置值");
        kernel.RegistryDeleteValue += data => Registry(data, "删除值");
        kernel.RegistryDelete += data => Registry(data, "删除键");
        kernel.RegistryEnumerateKey += data => Registry(data, "枚举键");

        kernel.ImageLoad += Tracked<ImageLoadTraceData>(data => Record(data, ActivityKind.Image, "加载", data.FileName, $"模块大小：{data.ImageSize:N0} 字节。"));
        kernel.TcpIpConnect += d => Network(d, "TCP/IPv4", "连接", d.saddr, d.sport, d.daddr, d.dport, 0, false);
        kernel.TcpIpAccept += d => Network(d, "TCP/IPv4", "接受连接", d.saddr, d.sport, d.daddr, d.dport, 0, true);
        kernel.TcpIpSend += d => Network(d, "TCP/IPv4", "发送", d.saddr, d.sport, d.daddr, d.dport, d.size, false);
        kernel.TcpIpRecv += d => Network(d, "TCP/IPv4", "接收", d.saddr, d.sport, d.daddr, d.dport, d.size, true);
        kernel.TcpIpDisconnect += d => Network(d, "TCP/IPv4", "断开", d.saddr, d.sport, d.daddr, d.dport, 0, false);
        kernel.TcpIpReconnect += d => Network(d, "TCP/IPv4", "重连", d.saddr, d.sport, d.daddr, d.dport, 0, false);
        kernel.TcpIpConnectIPV6 += d => Network(d, "TCP/IPv6", "连接", d.saddr, d.sport, d.daddr, d.dport, 0, false);
        kernel.TcpIpAcceptIPV6 += d => Network(d, "TCP/IPv6", "接受连接", d.saddr, d.sport, d.daddr, d.dport, 0, true);
        kernel.TcpIpSendIPV6 += d => Network(d, "TCP/IPv6", "发送", d.saddr, d.sport, d.daddr, d.dport, d.size, false);
        kernel.TcpIpRecvIPV6 += d => Network(d, "TCP/IPv6", "接收", d.saddr, d.sport, d.daddr, d.dport, d.size, true);
        kernel.TcpIpDisconnectIPV6 += d => Network(d, "TCP/IPv6", "断开", d.saddr, d.sport, d.daddr, d.dport, 0, false);
        kernel.UdpIpSend += d => Network(d, "UDP/IPv4", "发送", d.saddr, d.sport, d.daddr, d.dport, d.size, false);
        kernel.UdpIpRecv += d => Network(d, "UDP/IPv4", "接收", d.saddr, d.sport, d.daddr, d.dport, d.size, true);
        kernel.UdpIpSendIPV6 += d => Network(d, "UDP/IPv6", "发送", d.saddr, d.sport, d.daddr, d.dport, d.size, false);
        kernel.UdpIpRecvIPV6 += d => Network(d, "UDP/IPv6", "接收", d.saddr, d.sport, d.daddr, d.dport, d.size, true);
    }

    private Action<T> Tracked<T>(Action<T> handler) where T : TraceEvent => data =>
    {
        // Reject unrelated file/image events before resolving names or formatting strings.
        if (tracker!.Find(data.ProcessID) is not null) handler(data);
    };

    private void Registry(RegistryTraceData data, string operation)
    {
        if (tracker!.Find(data.ProcessID) is null) return;
        var (target, status) = registry.Resolve(data);
        Record(data, ActivityKind.Registry, operation, target,
            status.HasValue ? $"NTSTATUS：0x{status.Value:X8}；不采集注册表值内容。" : "完成状态未知；不采集注册表值内容。", status: status);
    }
    private void Network(TraceEvent data, string protocol, string operation, IPAddress source, int sourcePort, IPAddress destination, int destinationPort, long bytes, bool receive)
    {
        if (tracker!.Find(data.ProcessID) is null) return;
        var remote = receive ? source : destination;
        var port = receive ? sourcePort : destinationPort;
        // Count in the collector before event queues can omit records. Each process owns its bytes.
        if (operation == "发送") tracker.RecordNetworkSend(data.ProcessID, bytes, destination.ToString());
        Record(data, ActivityKind.Network, operation, $"{Endpoint(source, sourcePort)} → {Endpoint(destination, destinationPort)}",
            $"{protocol} · {Math.Max(0, bytes):N0} 字节 · {(RiskEngine.IsLocalAddress(remote.ToString()) ? "本机 / 局域网 / 特殊地址" : "公网地址")}\n只记录端点和传输字节，不读取通信内容。",
            Math.Max(0, bytes), remoteAddress: remote.ToString(), remotePort: port);
    }
    private static string Endpoint(IPAddress ip, int port) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
    private void Record(TraceEvent data, ActivityKind kind, string operation, string target, string detail, long bytes = 0, int? status = null, string remoteAddress = "", int remotePort = 0)
    {
        var process = tracker!.Find(data.ProcessID);
        if (process is null) return;
        Emit(new Activity
        {
            Time = new DateTimeOffset(data.TimeStamp),
            ProcessId = data.ProcessID,
            ProcessName = process.Name,
            Kind = kind,
            Operation = operation,
            Target = string.IsNullOrWhiteSpace(target) ? "[ETW 未解析出路径]" : target,
            Detail = detail,
            Bytes = bytes,
            Status = status,
            RemoteAddress = remoteAddress,
            RemotePort = remotePort
        });
    }
    private void Emit(Activity value) => ActivityReceived?.Invoke(RiskEngine.Evaluate(value with { Sequence = Interlocked.Increment(ref sequence) }));
    public void Stop()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        _ = EventsLost;
        session?.Stop(); // Allow Process() to drain the session's final buffers before it returns.
    }
    public void Dispose()
    {
        try { Stop(); }
        finally
        {
            if (!Completion.IsCompleted) session?.Source.StopProcessing();
            session?.Dispose();
        }
    }
}
