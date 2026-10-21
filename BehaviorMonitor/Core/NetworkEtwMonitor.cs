using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ProcessSentinel.Core.Networking;

public sealed class NetworkEtwMonitor : IDisposable
{
    private TraceEventSession? session;
    private readonly ConcurrentDictionary<int, ProcessInfo> processes = new();
    private readonly NetworkFlowTracker tracker = new(DateTimeOffset.UtcNow);
    private long cachedLoss;
    private int stopped;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        if (session is not null) throw new InvalidOperationException("网络采集器已经启动。");
        if (!EtwMonitor.IsAdministrator()) throw new UnauthorizedAccessException("网络流量采集需要管理员权限。");
        try
        {
            RefreshEndpoints();
            session = new TraceEventSession($"Kill-Network-{Environment.ProcessId}-{Guid.NewGuid():N}")
            { StopOnDispose = true, BufferSizeMB = 32 };
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
            var kernel = session.Source.Kernel;
            kernel.ProcessStart += data => processes[data.ProcessID] = ProcessCatalog.ReadDetails(new(data.ProcessID, data.ParentID,
                Path.GetFileName(data.ImageFileName), data.ImageFileName, data.TimeStamp.ToUniversalTime().Ticks));
            kernel.ProcessStop += data =>
            {
                tracker.ProcessExited(data.ProcessID, new DateTimeOffset(data.TimeStamp));
                processes.TryRemove(data.ProcessID, out _);
            };
            kernel.TcpIpSend += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, d.size, false);
            kernel.TcpIpRecv += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, d.size, true);
            kernel.TcpIpSendIPV6 += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, d.size, false);
            kernel.TcpIpRecvIPV6 += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, d.size, true);
            kernel.UdpIpSend += d => Transfer(d, NetworkTransport.UDP, d.saddr, d.sport, d.daddr, d.dport, d.size, false);
            kernel.UdpIpRecv += d => Transfer(d, NetworkTransport.UDP, d.saddr, d.sport, d.daddr, d.dport, d.size, true);
            kernel.UdpIpSendIPV6 += d => Transfer(d, NetworkTransport.UDP, d.saddr, d.sport, d.daddr, d.dport, d.size, false);
            kernel.UdpIpRecvIPV6 += d => Transfer(d, NetworkTransport.UDP, d.saddr, d.sport, d.daddr, d.dport, d.size, true);
            kernel.TcpIpConnect += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, 0, false);
            kernel.TcpIpAccept += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, 0, true);
            kernel.TcpIpConnectIPV6 += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, 0, false);
            kernel.TcpIpAcceptIPV6 += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, 0, true);
            kernel.TcpIpDisconnect += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, 0, false, true);
            kernel.TcpIpDisconnectIPV6 += d => Transfer(d, NetworkTransport.TCP, d.saddr, d.sport, d.daddr, d.dport, 0, false, true);
            Completion = Task.Factory.StartNew(() => session.Source.Process(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { Dispose(); throw; }
    }

    private void Transfer(TraceEvent data, NetworkTransport protocol, IPAddress source, int sourcePort,
        IPAddress destination, int destinationPort, long bytes, bool receive, bool close = false)
    {
        int pid = data.ProcessID;
        if (pid <= 0) return;
        var process = processes.GetOrAdd(pid, NetworkConnectionReader.ReadProcess);
        // TCP kernel events report saddr/sport as the local endpoint, including receive events.
        bool reverse = receive && protocol == NetworkTransport.UDP;
        var local = reverse ? destination : source;
        var remote = reverse ? source : destination;
        var endpoint = new NetworkEndpoint(protocol, pid, local.ToString(), reverse ? destinationPort : sourcePort,
            remote.ToString(), reverse ? sourcePort : destinationPort, protocol == NetworkTransport.TCP ? "ESTABLISHED" : "UDP_ACTIVE");
        ulong connectionId = 0;
        if (protocol == NetworkTransport.TCP && data.PayloadNames.Contains("connid"))
        {
            var value = data.PayloadByName("connid");
            if (value is ulong id) connectionId = id;
            else if (value is uint shortId) connectionId = shortId;
        }
        tracker.Record(endpoint, process, new DateTimeOffset(data.TimeStamp), Math.Max(0, bytes), receive, close, connectionId);
    }

    public NetworkSnapshot Capture(bool running = true)
    {
        string warning = "";
        if (running)
        {
            try { RefreshEndpoints(); }
            catch (Exception exception) { warning = "连接表刷新失败，连接状态暂保留上次结果：" + exception.Message; }
        }
        try { if (session is not null) cachedLoss = Math.Max(cachedLoss, session.EventsLost); } catch { }
        return tracker.Snapshot(DateTimeOffset.UtcNow, running, cachedLoss, warning);
    }

    private void RefreshEndpoints()
    {
        var endpoints = NetworkConnectionReader.Capture().Select(endpoint =>
        {
            if (endpoint.Process is not { } process) return endpoint;
            if (process.StartTimeUtcTicks == 0 && processes.TryGetValue(process.Id, out var known) && known.StartTimeUtcTicks > 0)
                return endpoint with { Process = known };
            processes[process.Id] = process;
            return endpoint;
        }).ToArray();
        tracker.Observe(endpoints, DateTimeOffset.UtcNow);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0) session?.Stop();
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
