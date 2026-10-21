using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessSentinel.Core;

public enum ActivityKind { File, Network, Registry, Process, Image, System }
public enum RiskLevel { None, Attention, High }

public sealed record Activity
{
    public long Sequence { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public ActivityKind Kind { get; init; }
    public string Operation { get; init; } = "";
    public string Target { get; init; } = "";
    public string Detail { get; init; } = "";
    public long Bytes { get; init; }
    public int? Status { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public RiskLevel Risk { get; init; }
    public string RuleId { get; init; } = "";
    public string Reason { get; init; } = "";
    [JsonIgnore]
    public long OutboundBytes => Kind == ActivityKind.Network && Operation == "发送" && !RiskEngine.IsLocalAddress(RemoteAddress)
        ? Math.Max(0, Bytes) : 0;
    [JsonIgnore] public string OutboundTrafficText => Kind == ActivityKind.Network ? ByteSize.Format(OutboundBytes) : "—";
    [JsonIgnore] public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss.fff");
    [JsonIgnore] public string KindText => Kind switch { ActivityKind.File => "文件", ActivityKind.Network => "网络", ActivityKind.Registry => "注册表", ActivityKind.Process => "进程", ActivityKind.Image => "模块", _ => "系统" };
    [JsonIgnore] public string RiskText => Risk switch { RiskLevel.High => "高关注", RiskLevel.Attention => "需复核", _ => "记录" };
    [JsonIgnore] public string ProcessText => $"{ProcessName} · {ProcessId}";
    [JsonIgnore] public string Evidence => string.IsNullOrEmpty(Reason) ? Detail : $"{Reason}\n\n{Detail}";
}

public sealed record ProcessInfo(int Id, int ParentId, string Name, string Path, long StartTimeUtcTicks)
{
    [JsonIgnore] public string Label => $"{Name}  ·  PID {Id}";
    [JsonIgnore] public string PathText => string.IsNullOrEmpty(Path) ? "路径不可读取（权限限制或进程已退出）" : Path;
    [JsonIgnore]
    public string StartTimeText => StartTimeUtcTicks > 0 && StartTimeUtcTicks <= DateTime.MaxValue.Ticks
        ? new DateTime(StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "未知";
}

public sealed record TrackedProcess(ProcessInfo Process, bool IsRoot, bool IsRunning, bool IsSelected = false, bool IsAncestor = false)
{
    public long OutboundBytes { get; init; }
    [JsonIgnore] public string OutboundTrafficText => ByteSize.Format(OutboundBytes);
    [JsonIgnore] public string RoleText => IsSelected ? IsRoot ? "所选/主" : "所选" : IsRoot ? "主进程" : IsAncestor ? "父进程" : "子进程";
    [JsonIgnore] public string StateText => IsRunning ? "运行中" : "已退出";
    [JsonIgnore]
    public string PathText => string.IsNullOrEmpty(Process.Path) ? Process.PathText
        : System.IO.Path.IsPathFullyQualified(Process.Path) ? Process.Path : $"{Process.Path}（完整路径未能读取）";
}

public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        double value = Math.Max(0, bytes);
        int i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }
}

public sealed record ProcessSnapshot(long Revision, IReadOnlyList<TrackedProcess> Processes)
{
    public int ActiveCount => Processes.Count(x => x.IsRunning);
}

public sealed record MonitorRequest(ProcessInfo Root, bool IncludeChildren, ProcessInfo? SelectedProcess = null);
public sealed record WireMessage(string Type, Activity? Event = null, string? Text = null, long EtwLost = 0, long QueueLost = 0, int ActiveProcesses = 0,
    IReadOnlyList<TrackedProcess>? Processes = null);

public static class Protocol
{
    public static string Version => typeof(Protocol).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json);
}
