using Kill;
using Kill.Models;
using Kill.Services;
using Microsoft.Data.Sqlite;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("运行状态按路径匹配且去重进程", () =>
    {
        var app = TestApplication("Example", installLocation: @"C:\RuntimeTest\Product");
        var result = ApplicationRuntimeService.Match([app],
            [new(101, @"c:\runtimetest\product\app.exe"), new(102, @"C:\RuntimeTest\Product\bin\helper.exe"),
             new(102, @"C:\RuntimeTest\Product\bin\helper.exe"), new(103, @"C:\RuntimeTest\Product Extra\app.exe")], DateTimeOffset.UtcNow)[app];
        return CheckAsync(result.State == ApplicationRunState.Running && result.Processes.Select(process => process.Id).SequenceEqual([101, 102]),
            "必须按完整路径边界识别主进程和子目录进程，不能匹配相似前缀或重复统计 PID");
    }),
    ("运行状态不根据进程名称猜测", () =>
    {
        var app = TestApplication("Example", installLocation: @"C:\RuntimeTest\Example");
        var unknown = TestApplication("No Evidence");
        var root = TestApplication("Drive Root", installLocation: @"C:\");
        var result = ApplicationRuntimeService.Match([app, unknown, root], [new(101, @"C:\Other\Example.exe")], DateTimeOffset.UtcNow);
        return CheckAsync(result[app].State == ApplicationRunState.NotDetected && result[unknown].State == ApplicationRunState.Unknown &&
                          result[root].State == ApplicationRunState.Unknown,
            "同名进程、缺少证据及磁盘根目录不能被误判为正在运行");
    }),
    ("运行状态支持图标可执行文件精确识别", () =>
    {
        var app = TestApplication("Icon", displayIcon: @"""C:\RuntimeTest\Icon\app.exe"",0");
        var dll = TestApplication("DLL Icon", displayIcon: @"C:\RuntimeTest\Icon\icons.dll,0");
        var result = ApplicationRuntimeService.Match([app, dll], [new(101, @"C:\RuntimeTest\Icon\APP.exe")], DateTimeOffset.UtcNow);
        return CheckAsync(result[app].IsRunning && result[dll].State == ApplicationRunState.Unknown,
            "可执行图标可提供精确路径证据，资源 DLL 不得当成应用进程");
    }),
    ("运行状态拒绝共用目录归属猜测", () =>
    {
        var first = TestApplication("First", installLocation: @"C:\RuntimeTest\Shared");
        var second = TestApplication("Second", installLocation: @"C:\RuntimeTest\Shared");
        var result = ApplicationRuntimeService.Match([first, second], [new(101, @"C:\RuntimeTest\Shared\helper.exe")], DateTimeOffset.UtcNow);
        return CheckAsync(result.Values.All(status => status.State == ApplicationRunState.Unknown && status.Processes.Count == 0),
            "共用同一路径的进程应显示归属不确定，不能将两个应用都标为运行中");
    }),
    ("运行状态优先使用精确程序及更具体目录", () =>
    {
        var parent = TestApplication("Parent", installLocation: @"C:\RuntimeTest\Vendor");
        var child = TestApplication("Child", installLocation: @"C:\RuntimeTest\Vendor\Child");
        var first = TestApplication("First", installLocation: @"C:\RuntimeTest\Shared", displayIcon: @"C:\RuntimeTest\Shared\first.exe");
        var second = TestApplication("Second", installLocation: @"C:\RuntimeTest\Shared", displayIcon: @"C:\RuntimeTest\Shared\second.exe");
        var result = ApplicationRuntimeService.Match([parent, child, first, second],
            [new(101, @"C:\RuntimeTest\Vendor\Child\helper.exe"), new(102, @"C:\RuntimeTest\Shared\first.exe")], DateTimeOffset.UtcNow);
        return CheckAsync(result[child].IsRunning && result[first].IsRunning &&
                          result[parent].State == ApplicationRunState.NotDetected && result[second].State == ApplicationRunState.NotDetected,
            "嵌套目录及共用目录的不同程序应归属到更明确的应用");
    }),
    ("运行状态支持商店应用且拒绝共享宿主", () =>
    {
        var storePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", "Example_1.0_x64");
        var store = new InstalledApplication { Id = "store", DisplayName = "Store", Kind = ApplicationKind.Store, InstallLocation = storePath };
        var host = TestApplication("Shared Host", displayIcon: Path.Combine(Environment.SystemDirectory, "rundll32.exe"));
        var uninstaller = new InstalledApplication { Id = "uninstaller", DisplayName = "Uninstall Only", UninstallCommand = @"C:\RuntimeTest\uninstall.exe" };
        var result = ApplicationRuntimeService.Match([store, host, uninstaller],
            [new(101, Path.Combine(storePath, "app.exe")), new(102, Path.Combine(Environment.SystemDirectory, "rundll32.exe")),
             new(103, @"C:\RuntimeTest\uninstall.exe")], DateTimeOffset.UtcNow);
        return CheckAsync(result[store].IsRunning && result[host].State == ApplicationRunState.Unknown && result[uninstaller].State == ApplicationRunState.Unknown,
            "商店应用应使用具体包目录；通用宿主或卸载命令不能代表应用正在运行");
    }),
    ("运行状态明确保留权限限制", () =>
    {
        var app = TestApplication("Example", installLocation: @"C:\RuntimeTest\Example");
        var result = ApplicationRuntimeService.Match([app], [new(101, "")], DateTimeOffset.UtcNow, 1)[app];
        return CheckAsync(result.State == ApplicationRunState.NotDetected && result.Description.Contains("无法读取") && result.CheckedAtUtc is not null,
            "无法读取路径时不得按名称猜测或宣称应用确定未运行");
    }),
    ("本机应用运行状态只读识别", () =>
    {
        var app = TestApplication("SelfTest", displayIcon: Environment.ProcessPath ?? throw new InvalidOperationException("无法读取自身路径"));
        var result = new ApplicationRuntimeService().Capture([app])[app];
        return CheckAsync(result.IsRunning && result.Processes.Any(process => process.Id == Environment.ProcessId),
            "应通过只读进程快照识别自身进程");
    }),
    ("工作进程拒绝目录穿越与外部请求", () =>
    {
        foreach (var path in new[] { @"C:\outside.json", Path.Combine(KillPaths.TempRoot, "..", "outside.json"), Path.Combine(KillPaths.TempRoot, "nested", "request.json"), Path.Combine(KillPaths.TempRoot, "request.json:stream") })
            ExpectRejected(() => WorkerInputPolicy.ValidateTemporaryPath(path));
        WorkerInputPolicy.ValidateTemporaryPath(Path.Combine(KillPaths.TempRoot, "synthetic-request.json"));
        return Task.CompletedTask;
    }),
    ("工作进程拒绝操作和候选标识注入", () =>
    {
        ExpectRejected(() => WorkerInputPolicy.ValidateOperationId(@"..\outside"));
        ExpectRejected(() => WorkerInputPolicy.ValidateItem(new CleanupCandidateDto
        {
            Id = @"..\outside", Kind = CleanupItemKind.File, Target = @"C:\Test\example.txt", Reason = "test", Risk = RiskLevel.Low
        }));
        WorkerInputPolicy.ValidateOperationId("20261021-120000-0123456789abcdef0123456789abcdef");
        return Task.CompletedTask;
    }),
    ("恢复清单必须匹配历史目录和操作标识", () =>
    {
        const string operation = "20261021-120000-0123456789abcdef0123456789abcdef";
        WorkerInputPolicy.ValidateManifestPath(Path.Combine(KillPaths.HistoryRoot, operation + ".json"), operation);
        ExpectRejected(() => WorkerInputPolicy.ValidateManifestPath(Path.Combine(KillPaths.HistoryRoot, "other.json"), operation));
        ExpectRejected(() => WorkerInputPolicy.ValidateManifestPath(Path.Combine(KillPaths.TempRoot, operation + ".json"), operation));
        return Task.CompletedTask;
    }),
    ("恢复拒绝范围外备份和受保护目标", () =>
    {
        const string operation = "20261021-120000-0123456789abcdef0123456789abcdef";
        ExpectRejected(() => WorkerInputPolicy.ValidateRestoreEntry(operation, new OperationEntry
        {
            Kind = CleanupItemKind.File, OriginalTarget = Path.Combine(Environment.SystemDirectory, "example.exe"), BackupTarget = @"C:\other\backup.exe"
        }));
        ExpectRejected(() => WorkerInputPolicy.ValidateRestoreEntry(operation, new OperationEntry
        {
            Kind = CleanupItemKind.RegistryKey, OriginalTarget = @"HKCU\SOFTWARE\Example", BackupTarget = Path.Combine(KillPaths.TempRoot, "0123456789abcdef0123456789abcdef.reg")
        }));
        return Task.CompletedTask;
    }),
    ("注册表恢复拒绝备份内的越界键", async () =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "Kill-registry-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string backup = Path.Combine(directory, "test.reg");
            await File.WriteAllTextAsync(backup, "Windows Registry Editor Version 5.00\n[HKEY_CURRENT_USER\\SOFTWARE\\Example Product]\n\"Value\"=\"test\"\n", System.Text.Encoding.Unicode);
            WorkerInputPolicy.ValidateRegistryBackup(backup, @"HKCU\SOFTWARE\Example Product");
            await File.AppendAllTextAsync(backup, "[HKEY_CURRENT_USER\\SOFTWARE\\Another Product]\n", System.Text.Encoding.Unicode);
            ExpectRejected(() => WorkerInputPolicy.ValidateRegistryBackup(backup, @"HKCU\SOFTWARE\Example Product"));
        }
        finally { Directory.Delete(directory, true); }
    }),
    ("注册表工作进程保护共享及系统配置", () =>
    {
        foreach (string target in new[] { @"HKLM\SOFTWARE\Microsoft", @"HKCU\SOFTWARE\Classes\Example", @"HKLM\SOFTWARE\Policies\Example", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" })
        {
            bool rejected = false;
            try { CleanupWorker.ParseAndValidateRegistryTarget(target); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("共享配置未被保护：" + target);
        }
        return Task.CompletedTask;
    }),
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
    ("安装目录精确反向识别", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillSelfTest", "Example Product");
        var application = TestApplication("Example Product", installLocation: directory);
        var matches = new InstallDirectoryIdentificationService().FindMatches(directory, [application]);
        return CheckAsync(matches.Count == 1 && matches[0].Application == application &&
                          matches[0].CanUseSelectedDirectoryForCleanup &&
                          matches[0].Evidence.Contains("安装登记位置完全一致"),
            "登记安装位置完全一致时应识别应用并允许将目录加入高风险复核");
    }),
    ("安装目录路径证据识别", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillSelfTest", "Icon Product");
        var application = TestApplication("Icon Product",
            displayIcon: $"\"{Path.Combine(directory, "product.exe")}\",0");
        var matches = new InstallDirectoryIdentificationService().FindMatches(directory, [application]);
        return CheckAsync(matches.Count == 1 && matches[0].CanUseSelectedDirectoryForCleanup &&
                          matches[0].Evidence.Any(item => item.Contains("显示图标直接位于", StringComparison.Ordinal)),
            "显示图标直接位于所选目录时应形成强路径证据");
    }),
    ("共享安装目录禁止整体清理", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillSelfTest", "Shared Vendor");
        var matches = new InstallDirectoryIdentificationService().FindMatches(directory,
            [TestApplication("Product A", installLocation: directory), TestApplication("Product B", installLocation: directory)]);
        return CheckAsync(matches.Count == 2 && matches.All(match => !match.CanUseSelectedDirectoryForCleanup),
            "多个应用命中同一目录时不得把该目录作为整体清理候选");
    }),
    ("深层目录证据只用于识别", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillSelfTest", "Vendor Root");
        var iconPath = Path.Combine(directory, "Product", "product.exe");
        var matches = new InstallDirectoryIdentificationService().FindMatches(directory,
            [TestApplication("Nested Product", displayIcon: $"\"{iconPath}\",0")]);
        return CheckAsync(matches.Count == 1 && !matches[0].CanUseSelectedDirectoryForCleanup,
            "仅命中深层子目录时可识别应用，但不得整体清理上层目录");
    }),
    ("拒绝受保护目录识别", () =>
    {
        try
        {
            _ = new InstallDirectoryIdentificationService().FindMatches(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                [TestApplication("Unsafe Product")]);
            return CheckAsync(false, "Windows 目录不应进入安装目录识别流程");
        }
        catch (InvalidOperationException)
        {
            return CheckAsync(true, "");
        }
    }),
    ("目录名称不得替代路径证据", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillSelfTest", "Same Name Product");
        var matches = new InstallDirectoryIdentificationService().FindMatches(directory,
            [TestApplication("Same Name Product")]);
        return CheckAsync(matches.Count == 0, "仅文件夹名称与应用名称相同不得形成匹配");
    }),
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
    ("版本号约定", () =>
    {
        var parts = ProductInfo.Version.Split('.');
        var valid = parts.Length == 3 && parts.All(part => int.TryParse(part, out _)) &&
                    int.Parse(parts[2]) is >= 10 and <= 99;
        var versionFile = Path.Combine(AppContext.BaseDirectory, "VERSION");
        return CheckAsync(valid && File.Exists(versionFile) &&
                          File.ReadAllText(versionFile).Trim().Equals(ProductInfo.Version, StringComparison.Ordinal),
            "程序集与 VERSION 文件必须一致，并使用 x.x.xx（10-99）格式");
    }),
    ("资源监控时长计算", () =>
    {
        var limited = new ResourceMonitorOptions(ResourceMetricSelection.Cpu,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));
        var unlimited = limited with { Duration = null };
        return CheckAsync(
            !ResourceMonitorTiming.HasReachedDuration(limited.Duration, TimeSpan.FromMinutes(29)) &&
            ResourceMonitorTiming.HasReachedDuration(limited.Duration, TimeSpan.FromMinutes(30)) &&
            ResourceMonitorTiming.GetWakeDelay(limited, TimeSpan.FromMinutes(31),
                TimeSpan.FromMinutes(29.9)) == TimeSpan.FromSeconds(6) &&
            ResourceMonitorTiming.GetWakeDelay(unlimited, TimeSpan.FromSeconds(31),
                TimeSpan.FromSeconds(30)) == TimeSpan.FromSeconds(1) &&
            ResourceMonitorTiming.AdvanceSampleDeadline(TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6.2)) == TimeSpan.FromSeconds(11),
            "限时监控必须按截止时间停止，采样计划必须按绝对时间补偿执行耗时");
    }),
    ("进程 Top 10 排序", () =>
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var snapshot = new ProcessResourceSnapshot(capturedAt, 1,
        [
            new ProcessResourceSample
            {
                ProcessName = "Alpha", ProcessId = 1, CapturedAtUtc = capturedAt,
                CpuPercent = 20, WorkingSetBytes = 300, IoReadBytesPerSecond = 10,
                IoWriteBytesPerSecond = 20
            },
            new ProcessResourceSample
            {
                ProcessName = "Bravo", ProcessId = 2, CapturedAtUtc = capturedAt,
                CpuPercent = 80, WorkingSetBytes = 100, IoReadBytesPerSecond = 5,
                IoWriteBytesPerSecond = 5, IoOtherBytesPerSecond = 100
            },
            new ProcessResourceSample
            {
                ProcessName = "Charlie", ProcessId = 3, CapturedAtUtc = capturedAt,
                CpuPercent = null, WorkingSetBytes = 500, IoReadBytesPerSecond = 40,
                IoWriteBytesPerSecond = 30
            }
        ]);

        var cpu = snapshot.GetTop(2, ProcessResourceSort.Cpu);
        var memory = snapshot.GetTop(2, ProcessResourceSort.Memory);
        var io = snapshot.GetTop(2, ProcessResourceSort.IoTotal);
        return CheckAsync(
            cpu.Select(process => process.ProcessName).SequenceEqual(["Bravo", "Alpha"]) &&
            memory.Select(process => process.ProcessName).SequenceEqual(["Charlie", "Alpha"]) &&
            io.Select(process => process.ProcessName).SequenceEqual(["Bravo", "Charlie"]) &&
            cpu.Select(process => process.Rank).SequenceEqual([1, 2]),
            "Top 10 必须按所选指标降序排列、排除无数据项并重新编号");
    }),
    ("本机应用只读枚举", async () =>
    {
        var applications = await new ApplicationDiscoveryService().GetApplicationsAsync();
        if (applications.Count == 0) throw new InvalidOperationException("没有读取到任何应用");
        if (applications.Any(app => string.IsNullOrWhiteSpace(app.DisplayName)))
            throw new InvalidOperationException("枚举结果包含空名称");
        if (!applications.Any(app => app.Kind == Kill.Models.ApplicationKind.Desktop))
            throw new InvalidOperationException("没有读取到桌面应用");
        var pathCandidate = applications.FirstOrDefault(app => app.Kind == ApplicationKind.Desktop &&
                                                               Directory.Exists(app.InstallLocation) &&
                                                               SafetyPolicy.IsPathSafeToQuarantine(app.InstallLocation));
        if (pathCandidate is not null)
        {
            var matches = new InstallDirectoryIdentificationService()
                .FindMatches(pathCandidate.InstallLocation, applications);
            if (!matches.Any(match => match.Application.Id == pathCandidate.Id))
                throw new InvalidOperationException("未能按真实安装目录反向识别对应应用");
            Console.WriteLine($"      安装目录识别到 {matches.Count} 个候选应用");
        }
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
    }),
    ("本机资源只读采样", async () =>
    {
        const ResourceMetricSelection metrics = ResourceMetricSelection.Cpu |
                                                ResourceMetricSelection.Memory |
                                                ResourceMetricSelection.Disk |
                                                ResourceMetricSelection.Network;
        using var sampler = new ResourceMetricsSampler(metrics);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var sample = sampler.Capture(1);
        if (sample.SampleDurationSeconds < 1)
            throw new InvalidOperationException("采样间隔异常");
        if (sample.CpuPercent is < 0 or > 100)
            throw new InvalidOperationException("CPU 使用率超出有效范围");
        if (sample.MemoryUsedBytes is null || sample.MemoryTotalBytes is null ||
            sample.MemoryUsedBytes < 0 || sample.MemoryUsedBytes > sample.MemoryTotalBytes)
            throw new InvalidOperationException("物理内存数据无效");
        if (sample.MemoryPercent is < 0 or > 100)
            throw new InvalidOperationException("内存使用率超出有效范围");
        if (sample.DiskReadBytesPerSecond is < 0 || sample.DiskWriteBytesPerSecond is < 0 ||
            sample.NetworkReceivedBytesPerSecond is < 0 || sample.NetworkSentBytesPerSecond is < 0)
            throw new InvalidOperationException("I/O 速率不得为负数");
        Console.WriteLine($"      CPU {sample.CpuLabel}，内存 {sample.MemoryPercentLabel}");
    }),
    ("本机进程资源只读采样", async () =>
    {
        var sampler = new ProcessResourceSampler();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        var snapshot = sampler.Capture();
        if (snapshot.Processes.Count == 0)
            throw new InvalidOperationException("没有读取到任何进程资源");
        if (snapshot.Processes.Any(process => process.CpuPercent is < 0 or > 100 ||
                                              process.WorkingSetBytes is < 0 ||
                                              process.IoReadBytesPerSecond is < 0 ||
                                              process.IoWriteBytesPerSecond is < 0 ||
                                              process.IoOtherBytesPerSecond is < 0))
            throw new InvalidOperationException("进程资源数据超出有效范围");

        var top = snapshot.GetTop(10, ProcessResourceSort.Cpu);
        if (top.Count > 10 || !top.Select(process => process.Rank).SequenceEqual(
                Enumerable.Range(1, top.Count)))
            throw new InvalidOperationException("进程 Top 10 数量或排名编号无效");
        Console.WriteLine($"      只读采样到 {snapshot.Processes.Count} 个进程");
    }),
    ("SQLite 监控记录往返", async () =>
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "KillSelfTest", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testRoot, "monitor.db");
        try
        {
            var repository = new ResourceMonitorRepository(databasePath);
            await repository.InitializeAsync();
            var options = new ResourceMonitorOptions(
                ResourceMetricSelection.Cpu | ResourceMetricSelection.Memory, TimeSpan.FromSeconds(5),
                TimeSpan.FromMinutes(30));
            var sessionId = await repository.StartSessionAsync(options);
            if (!await repository.HasHistoryAsync())
                throw new InvalidOperationException("已创建监控会话但未识别到历史数据");
            var capturedAt = DateTimeOffset.UtcNow;
            await repository.SaveSampleAsync(new ResourceSample
            {
                SessionId = sessionId,
                CapturedAtUtc = capturedAt,
                SampleDurationSeconds = 5,
                CpuPercent = 12.5,
                MemoryUsedBytes = 4L * 1024 * 1024 * 1024,
                MemoryTotalBytes = 8L * 1024 * 1024 * 1024,
                MemoryPercent = 50
            });
            await repository.EndSessionAsync(sessionId, "测试完成");

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT duration_seconds, stop_reason, app_version,
                           (SELECT user_version FROM pragma_user_version)
                    FROM monitor_sessions
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", sessionId);
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync() || reader.GetInt64(0) != 1800 ||
                    reader.GetString(1) != "测试完成" || reader.GetString(2) != ProductInfo.Version ||
                    reader.GetInt64(3) != 2)
                    throw new InvalidOperationException("监控会话元数据或数据库版本不正确");
            }

            var records = await repository.LoadRecentSamplesAsync();
            if (await repository.GetSampleCountAsync() != 1 || records.Count != 1)
                throw new InvalidOperationException("监控样本数量不一致");
            var record = records[0];
            if (record.SessionId != sessionId || record.CpuPercent != 12.5 || record.MemoryPercent != 50)
                throw new InvalidOperationException("监控样本内容未正确恢复");

            await repository.ClearAsync();
            if (await repository.HasHistoryAsync())
                throw new InvalidOperationException("清空后仍被识别为存在历史数据");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM monitor_sessions), (SELECT COUNT(*) FROM resource_samples);";
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync() || reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0)
                    throw new InvalidOperationException("清空后仍有监控会话或样本");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }),
    ("SQLite 监控数据库兼容升级", async () =>
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "KillSelfTest", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testRoot, "legacy.db");
        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE monitor_sessions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        started_at_utc TEXT NOT NULL,
                        ended_at_utc TEXT,
                        interval_seconds INTEGER NOT NULL,
                        selected_metrics TEXT NOT NULL,
                        app_version TEXT NOT NULL
                    );
                    CREATE TABLE resource_samples (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id INTEGER NOT NULL,
                        captured_at_utc TEXT NOT NULL,
                        sample_duration_seconds REAL NOT NULL,
                        cpu_percent REAL,
                        memory_used_bytes INTEGER,
                        memory_total_bytes INTEGER,
                        memory_percent REAL,
                        disk_active_percent REAL,
                        disk_read_bytes_per_second REAL,
                        disk_write_bytes_per_second REAL,
                        network_received_bytes_per_second REAL,
                        network_sent_bytes_per_second REAL
                    );
                    INSERT INTO monitor_sessions
                        (id, started_at_utc, interval_seconds, selected_metrics, app_version)
                    VALUES (7, '2026-01-01T00:00:00+00:00', 5, 'Cpu', '1.1.10');
                    INSERT INTO resource_samples
                        (session_id, captured_at_utc, sample_duration_seconds, cpu_percent)
                    VALUES (7, '2026-01-01T00:00:05+00:00', 5, 25);
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new ResourceMonitorRepository(databasePath);
            await repository.InitializeAsync();
            await repository.InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT duration_seconds, stop_reason,
                           (SELECT COUNT(*) FROM resource_samples),
                           (SELECT user_version FROM pragma_user_version)
                    FROM monitor_sessions WHERE id = 7;
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync() || !reader.IsDBNull(0) || !reader.IsDBNull(1) ||
                    reader.GetInt64(2) != 1 || reader.GetInt64(3) != 2)
                    throw new InvalidOperationException("v1 数据库升级未保留旧记录或未正确升级结构");
            }

            var futurePath = Path.Combine(testRoot, "future.db");
            await using (var connection = new SqliteConnection($"Data Source={futurePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 99;";
                await command.ExecuteNonQueryAsync();
            }

            try
            {
                await new ResourceMonitorRepository(futurePath).InitializeAsync();
                throw new InvalidOperationException("程序接受了无法识别的未来数据库版本");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("高于当前程序支持", StringComparison.Ordinal))
            {
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    })
};

tests.AddRange(NetworkMonitorChecks.All());
tests.AddRange(NetworkHistoryChecks.All());
tests.AddRange(StartupChecks.All());
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

static InstalledApplication TestApplication(string name, string installLocation = "", string displayIcon = "") => new()
{
    Id = $"test:{name}",
    DisplayName = name,
    Publisher = "Example Corp",
    InstallLocation = installLocation,
    DisplayIcon = displayIcon,
    UninstallCommand = "msiexec.exe /X {00000000-0000-0000-0000-000000000000}",
    Kind = ApplicationKind.Desktop,
    Scope = InstallScope.AllUsers,
    CanUninstall = true
};

static void ExpectRejected(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("非法工作进程输入未被拒绝。");
}


sealed class NonPumpingSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
    {
        // Simulates a blocked WPF dispatcher. Worker continuations must not post here.
    }
}
