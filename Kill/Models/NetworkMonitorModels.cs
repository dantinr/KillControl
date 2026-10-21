using System.ComponentModel;
using ProcessSentinel.Core;
using ProcessSentinel.Core.Networking;

namespace Kill.Models;

public sealed class NetworkFlowRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public required NetworkFlow Flow { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string Id => Flow.Id;
    public string ProcessLabel => $"{Flow.Process.Name} · {Flow.Process.Id}";
    public string ProcessPath => string.IsNullOrEmpty(Flow.Process.Path) ? "进程路径无法读取或进程已退出" : Flow.Process.Path;
    public string Protocol => Flow.Endpoint.Protocol.ToString();
    public string AddressFamily => Flow.Endpoint.LocalAddress.Contains(':') ? "IPv6" : "IPv4";
    public string LocalEndpoint => NetworkAddress.Endpoint(Flow.Endpoint.LocalAddress, Flow.Endpoint.LocalPort);
    public string RemoteEndpoint => NetworkAddress.Endpoint(Flow.Endpoint.RemoteAddress, Flow.Endpoint.RemotePort);
    public bool IsEnded => Flow.EndedAtUtc is not null;
    public string State => DescribeState(Flow.Endpoint.State);
    public static string DescribeState(string state) => state switch
    {
        "LISTEN" => "监听",
        "ESTABLISHED" => "已连接",
        "SYN_SENT" => "连接中",
        "SYN_RECEIVED" => "握手中",
        "CLOSE_WAIT" => "等待关闭",
        "FIN_WAIT_1" or "FIN_WAIT_2" or "CLOSING" or "LAST_ACK" => "关闭中",
        "TIME_WAIT" => "TIME_WAIT",
        "CLOSED" or "DELETE_TCB" => "已关闭",
        "UDP_BOUND" => "UDP 端点",
        "UDP_ACTIVE" => "UDP 活动",
        "UDP_IDLE" => "UDP 空闲",
        "DISCONNECTED" => "已断开",
        "DISAPPEARED" => "未再发现",
        "PROCESS_EXITED" => "进程已退出",
        "REPLACED" => "连接已替换",
        "MONITOR_STOPPED" => "监控已停止",
        "COLLECTOR_INTERRUPTED" => "采集已中断",
        _ => state
    };
    public long SentBytes => Flow.SentBytes;
    public long ReceivedBytes => Flow.ReceivedBytes;
    public double SendRate => Flow.SendBytesPerSecond;
    public double ReceiveRate => Flow.ReceiveBytesPerSecond;
    public string SentText => ByteSize.Format(SentBytes);
    public string ReceivedText => ByteSize.Format(ReceivedBytes);
    public string SendRateText => ByteSize.Format((long)Math.Clamp(SendRate, 0, long.MaxValue)) + "/s";
    public string ReceiveRateText => ByteSize.Format((long)Math.Clamp(ReceiveRate, 0, long.MaxValue)) + "/s";
    public double DurationSeconds => Math.Max(0, ((Flow.EndedAtUtc ?? CapturedAtUtc) - Flow.FirstSeenUtc).TotalSeconds);
    public string Duration => FormatDuration(DurationSeconds);
    public string Detail => $"{ProcessLabel}\n{ProcessPath}\n{Protocol} / {AddressFamily} · {LocalEndpoint} → {RemoteEndpoint}\n" +
        $"首次观测：{Flow.FirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 观测时长：{Duration}\n" +
        $"累计发送：{SentBytes:N0} B · 累计接收：{ReceivedBytes:N0} B";

    public void Update(NetworkFlow flow, DateTimeOffset time)
    {
        Flow = flow;
        CapturedAtUtc = time;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    public static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds - 1));
        return duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}天 {duration:hh\\:mm\\:ss}" : duration.ToString("hh\\:mm\\:ss");
    }
}
