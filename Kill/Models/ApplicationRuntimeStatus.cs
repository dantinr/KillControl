namespace Kill.Models;

public enum ApplicationRunState
{
    Checking,
    Running,
    NotDetected,
    Unknown
}

public sealed record ApplicationProcess(int Id, string ExecutablePath);

public sealed record ApplicationRuntimeStatus(
    ApplicationRunState State,
    IReadOnlyList<ApplicationProcess> Processes,
    string Description,
    DateTimeOffset? CheckedAtUtc = null)
{
    public static ApplicationRuntimeStatus Checking { get; } = new(ApplicationRunState.Checking, [], "正在读取运行进程…");
    public bool IsRunning => State == ApplicationRunState.Running;
    public string Label => State switch
    {
        ApplicationRunState.Running => $"运行中（{Processes.Count}）",
        ApplicationRunState.NotDetected => "未检测到",
        ApplicationRunState.Unknown => "无法判断",
        _ => "检测中…"
    };
    public string Summary => Processes.Count == 0 ? Description :
        $"含后台进程 · PID {string.Join(", ", Processes.Take(12).Select(process => process.Id))}{(Processes.Count > 12 ? " …" : "")}";
    public string Details => Description +
        (Processes.Count == 0 ? "" : "\n" + string.Join("\n", Processes.Take(20).Select(process => $"PID {process.Id} · {process.ExecutablePath}"))) +
        (Processes.Count > 20 ? $"\n另有 {Processes.Count - 20} 个进程。" : "") +
        (CheckedAtUtc is { } time ? $"\n更新于 {time.ToLocalTime():HH:mm:ss}；页面可见时约每 3 秒刷新。" : "");
}
