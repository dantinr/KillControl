namespace ProcessSentinel.Core;

public sealed record ProcessFamily(ProcessInfo Root, ProcessInfo Selected, IReadOnlyList<ProcessInfo> Lineage, string Boundary)
{
    public MonitorRequest Request => new(Root, true, Selected);
    public string ScopeText => $"所选：{Selected.Label} · 监控：{Root.Label} 及全部子孙进程";
}

public static class ProcessFamilyResolver
{
    // These shared launchers/hosts do not identify one application family.
    private static readonly HashSet<string> sharedLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "explorer.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "WindowsTerminal.exe", "wt.exe", "OpenConsole.exe", "conhost.exe",
        "bash.exe", "sh.exe", "mintty.exe", "wsl.exe", "wslhost.exe",
        "services.exe", "svchost.exe", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "userinit.exe", "sihost.exe", "taskhostw.exe", "RuntimeBroker.exe", "ApplicationFrameHost.exe",
        "WmiPrvSE.exe", "dllhost.exe", "rundll32.exe", "msiexec.exe", "taskeng.exe",
        "Kill.exe", "ProcessSentinel.exe", "ProcessSentinel.Collector.exe"
    };

    private static bool IsVersionedHost(string name) => name.StartsWith("Kill-v", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(name[6..^4], out _);

    public static ProcessFamily Resolve(ProcessInfo selected, IEnumerable<ProcessInfo> snapshot)
    {
        var byId = snapshot.ToDictionary(x => x.Id);
        if (selected.StartTimeUtcTicks <= 0 || !byId.TryGetValue(selected.Id, out var current)
            || current.StartTimeUtcTicks != selected.StartTimeUtcTicks)
            throw new InvalidOperationException("所选进程已退出或 PID 已被复用，请刷新后重新选择。");
        var lineage = new List<ProcessInfo> { current };
        var seen = new HashSet<int> { current.Id };
        string boundary;
        while (true)
        {
            if (current.ParentId <= 0 || !byId.TryGetValue(current.ParentId, out var parent))
            { boundary = "上级进程已退出或不在当前快照中"; break; }
            if (!seen.Add(parent.Id)) { boundary = "父进程链存在循环"; break; }
            if (parent.StartTimeUtcTicks <= 0) { boundary = "上级进程的启动时间无法读取"; break; }
            if (parent.StartTimeUtcTicks > current.StartTimeUtcTicks)
            { boundary = "上级 PID 已被复用，不能归为同一程序"; break; }
            if (parent.Id <= 4 || (sharedLaunchers.Contains(parent.Name) || parent.Id == Environment.ProcessId || IsVersionedHost(parent.Name)))
            { boundary = $"公共启动器或系统宿主：{parent.Label}"; break; }
            lineage.Add(parent);
            current = parent;
        }
        return new(current, lineage[0], lineage.ToArray(), boundary);
    }
}
