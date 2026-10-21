using ProcessSentinel.Core.Networking;

namespace Kill.Models;

public sealed record NetworkHistoryProcessKey(string SessionId, int ProcessId, long StartTicks);

public sealed record NetworkHistoryProcess(long Id, NetworkHistoryProcessKey Key, string Name, string Path,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, long ConnectionCount)
{
    public string Label => $"{Name} · {Key.ProcessId}";
    public string LastSeen => LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Detail => $"{Label}\n{(string.IsNullOrEmpty(Path) ? "进程路径无法读取" : Path)}\n" +
        $"首次观测：{FirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 最后观测：{LastSeen}\n" +
        $"进程创建：{(Key.StartTicks > 0 ? new DateTimeOffset(Key.StartTicks, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "未知")} · {ConnectionCount:N0} 条连接记录";
}

public sealed record NetworkHistoryConnection(string Id, string Protocol, string LocalAddress, int LocalPort,
    string RemoteAddress, int RemotePort, string State, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc,
    DateTimeOffset LastActivityUtc, DateTimeOffset? EndedAtUtc, long SentBytes, long ReceivedBytes)
{
    public string LocalEndpoint => NetworkAddress.Endpoint(LocalAddress, LocalPort);
    public string FirstSeen => FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string LastSeen => LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Sent => ProcessSentinel.Core.ByteSize.Format(SentBytes);
    public string Received => ProcessSentinel.Core.ByteSize.Format(ReceivedBytes);
    public string StateText => NetworkFlowRow.DescribeState(State);
    public string Detail => $"{Protocol} · {LocalEndpoint} → {NetworkAddress.Endpoint(RemoteAddress, RemotePort)}\n" +
        $"首次观测：{FirstSeen} · 最后观测：{LastSeen}\n最后活动：{LastActivityUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · " +
        $"结束观测：{(EndedAtUtc is { } end ? end.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "当时仍在观测，之后状态未知")}\n" +
        $"累计发送：{SentBytes:N0} B · 累计接收：{ReceivedBytes:N0} B · 最后状态：{StateText}";
}

public sealed record NetworkHistoryPage<T>(IReadOnlyList<T> Rows, long TotalCount);
