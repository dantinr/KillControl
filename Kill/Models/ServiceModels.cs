namespace Kill.Models;

public enum ServiceActionKind
{
    Disable,
    AddToBlacklist,
    RemoveFromBlacklist,
    Delete
}

public sealed class ManagedService
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public string State { get; init; } = "Unknown";
    public string StartMode { get; init; } = "Unknown";
    public int ProcessId { get; init; }
    public string ImageCommand { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string ServiceDllPath { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public string StartAccount { get; init; } = "";
    public string ServiceType { get; init; } = "";
    public string CompanyName { get; init; } = "未知发布者";
    public bool IsProtected { get; init; }
    public bool IsBlacklisted { get; init; }
    public bool AcceptsStop { get; init; }

    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public string StateLabel => IsRunning ? "运行中" : State.Equals("Stopped", StringComparison.OrdinalIgnoreCase) ? "已停止" : State;
    public string StartModeLabel => StartMode switch
    {
        "Auto" => "自动",
        "Manual" => "手动",
        "Disabled" => "已禁用",
        _ => StartMode
    };
    public string ClassificationLabel => IsProtected ? "系统保护" : "第三方服务";
    public string BlacklistLabel => IsBlacklisted ? "黑名单" : "";
    public string HostPath => !string.IsNullOrWhiteSpace(ServiceDllPath) ? ServiceDllPath : ExecutablePath;
    public string ProcessIdLabel => ProcessId > 0 ? ProcessId.ToString() : "未运行";
    public string Initials
    {
        get
        {
            var value = string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
            return value[..Math.Min(2, value.Length)].ToUpperInvariant();
        }
    }
}

public sealed class ServiceBlacklistEntry
{
    public required string ServiceName { get; init; }
    public required string DisplayName { get; init; }
    public int OriginalStartValue { get; init; }
    public bool OriginalDelayedAutoStart { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ServiceActionRequest
{
    public required string ServiceName { get; init; }
    public required ServiceActionKind Action { get; init; }
    public required string ResponsePath { get; init; }
}

public sealed class ServiceActionResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string BackupPath { get; init; } = "";
}
