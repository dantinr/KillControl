using Kill.Services;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("保护 Windows 目录", () => CheckAsync(
        SafetyPolicy.IsProtectedInstallLocation(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
        "Windows 目录必须被标记为受保护")),
    ("拒绝磁盘根目录", () => CheckAsync(
        !SafetyPolicy.IsPathSafeToQuarantine(Path.GetPathRoot(Environment.SystemDirectory)!),
        "不得把磁盘根目录移入隔离区")),
    ("拒绝 Windows 子目录", () => CheckAsync(
        !SafetyPolicy.IsPathSafeToQuarantine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Example")),
        "不得清理 Windows 下的任何子目录")),
    ("允许明确应用数据目录", () => CheckAsync(
        SafetyPolicy.IsPathSafeToQuarantine(Path.Combine(Path.GetTempPath(), "KillSelfTestSample")),
        "普通、名称明确的应用目录应能进入复核流程")),
    ("名称规范化精确匹配", () => CheckAsync(
        SafetyPolicy.IsStrongNameMatch("Example-App", "Example App"),
        "标点差异应被正确规范化")),
    ("拒绝宽泛名称匹配", () => CheckAsync(
        !SafetyPolicy.IsStrongNameMatch("App", "Application Suite"),
        "残留扫描不得使用模糊包含匹配")),
    ("Windows 命令行解析", () => CheckAsync(
        WindowsCommandLine.Split("\"C:\\Program Files\\Example\\uninstall.exe\" /remove \"user data\"")
            .SequenceEqual([@"C:\Program Files\Example\uninstall.exe", "/remove", "user data"]),
        "带空格和引号的卸载命令必须无损解析")),
    ("未加引号的卸载路径", () =>
    {
        var dotnetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var resolved = WindowsCommandLine.ResolveExecutable($"{dotnetPath} --info \"user data\"");
        return CheckAsync(
            resolved.Executable.Equals(dotnetPath, StringComparison.OrdinalIgnoreCase) &&
            resolved.Arguments.SequenceEqual(["--info", "user data"]),
            "未加引号且含空格的可执行文件路径必须完整识别");
    }),
    ("工作进程不捕获 UI 上下文", async () =>
    {
        var execution = Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            Kill.App.RunWorker(async () => await Task.Yield());
        });
        await execution.WaitAsync(TimeSpan.FromSeconds(2));
    }),
    ("保护 Windows 宿主服务", () => CheckAsync(
        ServiceSafetyPolicy.IsProtected("ExampleSystemService",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "example.dll"),
            "Share Process", "Microsoft Corporation"),
        "Windows 宿主服务必须被标记为系统保护")),
    ("保护驱动服务", () => CheckAsync(
        ServiceSafetyPolicy.IsProtected("ExampleDriver", @"C:\Program Files\Example\driver.sys", "", "Kernel Driver", "Example Corp"),
        "驱动程序不得进入可删除服务范围")),
    ("允许明确第三方服务", () => CheckAsync(
        !ServiceSafetyPolicy.IsProtected("ExampleAgent", @"C:\Program Files\Example\agent.exe", "", "Own Process", "Example Corp"),
        "宿主明确位于第三方程序目录的普通服务应可进入管理范围")),
    ("本机应用只读枚举", async () =>
    {
        var applications = await new ApplicationDiscoveryService().GetApplicationsAsync();
        if (applications.Count == 0) throw new InvalidOperationException("没有读取到任何应用");
        if (applications.Any(app => string.IsNullOrWhiteSpace(app.DisplayName)))
            throw new InvalidOperationException("枚举结果包含空名称");
        if (!applications.Any(app => app.Kind == Kill.Models.ApplicationKind.Desktop))
            throw new InvalidOperationException("没有读取到桌面应用");
        Console.WriteLine($"      只读枚举到 {applications.Count} 个应用");
    }),
    ("本机服务只读枚举", async () =>
    {
        var services = await new ServiceDiscoveryService().GetServicesAsync();
        if (services.Count == 0) throw new InvalidOperationException("没有读取到任何服务");
        if (services.Any(service => string.IsNullOrWhiteSpace(service.Name)))
            throw new InvalidOperationException("枚举结果包含空服务名称");
        if (!services.Any(service => service.IsProtected))
            throw new InvalidOperationException("没有识别到任何系统保护服务");
        Console.WriteLine($"      只读枚举到 {services.Count} 个服务，{services.Count(service => !service.IsProtected)} 个可管理");
    })
};

var failed = 0;
Console.WriteLine("Kill 安全自检\n");
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[通过] {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"[失败] {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"\n结果：{tests.Count - failed}/{tests.Count} 通过");
return failed == 0 ? 0 : 1;

static Task CheckAsync(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    return Task.CompletedTask;
}

sealed class NonPumpingSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
    {
        // Simulates a blocked WPF dispatcher. Worker continuations must not post here.
    }
}
