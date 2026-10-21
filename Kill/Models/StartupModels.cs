using Kill.Services;
using Microsoft.Win32;

namespace Kill.Models;

public enum StartupSource { UserRun, MachineRun64, MachineRun32, UserFolder, CommonFolder }
public enum StartupApproval { Registered, WindowsDisabled, Unknown }

public sealed record StartupEntry
{
    public StartupSource Source { get; init; }
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public RegistryValueKind ValueKind { get; init; } = RegistryValueKind.String;
    public string FileHash { get; init; } = "";
    public StartupApproval Approval { get; init; }
    public bool IsDisabled { get; init; }
    public bool HasConflict { get; init; }
    public string ProtectionReason { get; init; } = "";
    public string Id => StartupPolicy.Identity(Source, Name);
    public bool IsMachine => Source is StartupSource.MachineRun64 or StartupSource.MachineRun32 or StartupSource.CommonFolder;
    public bool IsFolder => Source is StartupSource.UserFolder or StartupSource.CommonFolder;
    public string ScopeLabel => IsMachine ? "所有用户" : "当前用户";
    public string SourceLabel => Source switch
    {
        StartupSource.UserRun => "注册表 · 当前用户",
        StartupSource.MachineRun64 => "注册表 · 64 位",
        StartupSource.MachineRun32 => "注册表 · 32 位",
        StartupSource.UserFolder => "启动文件夹 · 当前用户",
        _ => "启动文件夹 · 所有用户"
    };
    public string Location => StartupPolicy.Location(Source, Name);
    public string StatusLabel => HasConflict ? "备份冲突" : IsDisabled ? "已禁用（Kill）" : Approval switch
    {
        StartupApproval.WindowsDisabled => "Windows 已禁用",
        StartupApproval.Unknown => "状态待确认",
        _ => "已注册"
    };
    public string ActionNote => HasConflict ? "原位置已有同名项目或备份不完整；保留双方，请检查后手动处理。" :
        IsDisabled ? "恢复原始启动项，下次登录生效；不会立即运行程序。" :
        ProtectionReason.Length > 0 ? ProtectionReason : Approval != StartupApproval.Registered ?
            "此项目由 Windows 控制或状态无法确认，请通过右上角 Windows 启动设置调整。" :
            "禁用前保存原始启动项，可在本页恢复。不会终止正在运行的程序。";
    public bool CanDisable => !IsDisabled && !HasConflict && ProtectionReason.Length == 0 && Approval == StartupApproval.Registered;
    public bool CanRestore => IsDisabled && !HasConflict;
}

public sealed record StartupBackup(StartupEntry Entry, DateTimeOffset DisabledAt);
public sealed record StartupSnapshot(IReadOnlyList<StartupEntry> Entries, IReadOnlyList<string> Warnings);
public sealed record StartupActionRequest(StartupEntry Entry, bool Restore, string UserSid, string ResponsePath);
public sealed record StartupActionResult(bool Success, string Message);
