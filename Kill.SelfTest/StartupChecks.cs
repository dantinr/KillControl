using System.Text.Json;
using Kill.Models;
using Kill.Services;
using Microsoft.Win32;

internal static class StartupChecks
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return Check("启动项身份区分用户、注册表视图与文件夹且忽略名称大小写", () =>
        {
            Require(StartupPolicy.Identity(StartupSource.UserRun, "Example") == StartupPolicy.Identity(StartupSource.UserRun, "EXAMPLE"));
            Require(Enum.GetValues<StartupSource>().Select(source => StartupPolicy.Identity(source, "Example")).Distinct().Count() == 5);
        });
        yield return Check("启动项拒绝越界文件名、系统元数据和非法来源", () =>
        {
            foreach (string name in new[] { @"..\outside.exe", @"C:\outside.exe", "..", "desktop.ini", "file.exe:stream", "" })
                Reject(() => StartupPolicy.Validate(new() { Source = StartupSource.UserFolder, Name = name }));
            Reject(() => StartupPolicy.Validate(new() { Source = (StartupSource)999, Name = "Example" }));
            Reject(() => StartupPolicy.Validate(new() { Source = StartupSource.UserRun, Name = "Example", ValueKind = RegistryValueKind.Binary }));
            StartupPolicy.Validate(Entry());
        });
        yield return Check("启动项修改前复核命令、值类型、文件哈希与受保护状态", () =>
        {
            var expected = Entry();
            StartupPolicy.ValidateDisable(expected, expected);
            Reject(() => StartupPolicy.ValidateDisable(expected, expected with { Command = "changed" }));
            Reject(() => StartupPolicy.ValidateDisable(expected, expected with { ValueKind = RegistryValueKind.ExpandString }));
            Reject(() => StartupPolicy.ValidateDisable(expected, null));
            Reject(() => StartupPolicy.ValidateDisable(expected, expected with { ProtectionReason = "protected" }));
            Reject(() => StartupPolicy.ValidateDisable(expected, expected with { Approval = StartupApproval.Unknown }));
            var file = expected with { Source = StartupSource.UserFolder, Name = "Example.lnk", FileHash = "original" };
            StartupPolicy.ValidateDisable(file, file);
            Reject(() => StartupPolicy.ValidateDisable(file, file with { FileHash = "changed" }));
        });
        yield return Check("启动项恢复拒绝同名项目及已改变的备份", () =>
        {
            var entry = Entry();
            StartupPolicy.ValidateRestore(entry, entry, false);
            Reject(() => StartupPolicy.ValidateRestore(entry, entry, true));
            Reject(() => StartupPolicy.ValidateRestore(entry, entry with { Command = "changed" }, false));
            Reject(() => StartupPolicy.ValidateRestore(entry, entry with { Source = StartupSource.MachineRun64 }, false));
        });
        yield return Check("Windows 启动状态仅识别已知格式，未知数据不会开放修改", () =>
        {
            Require(StartupDiscoveryService.InterpretApproval(null) == StartupApproval.Registered);
            foreach (uint state in new uint[] { 2, 6, 3, 7, 99 })
            {
                var bytes = new byte[12];
                BitConverter.GetBytes(state).CopyTo(bytes, 0);
                Require(StartupDiscoveryService.InterpretApproval(bytes) == (state is 2 or 6 ? StartupApproval.Registered : state is 3 or 7 ? StartupApproval.WindowsDisabled : StartupApproval.Unknown));
            }
            Require(StartupDiscoveryService.InterpretApproval([2]) == StartupApproval.Unknown);
            Require(!(Entry() with { Approval = StartupApproval.WindowsDisabled }).CanDisable);
            Require(!(Entry() with { HasConflict = true, IsDisabled = true }).CanRestore);
        });
        yield return Check("启动项备份保留原始环境变量、引号与注册值类型", () =>
        {
            var entry = Entry() with { Command = "\"%LOCALAPPDATA%\\示例 程序\\app.exe\" --profile \"工作 配置\"", ValueKind = RegistryValueKind.ExpandString };
            var record = new StartupBackup(entry, DateTimeOffset.UtcNow);
            var decoded = JsonSerializer.Deserialize<StartupBackup>(JsonSerializer.Serialize(record))!;
            Require(StartupPolicy.SameContent(entry, decoded.Entry) && decoded.DisabledAt == record.DisabledAt);
        });
        yield return Check("启动项备份路径固定在各自作用域且标识不包含原始名称", () =>
        {
            foreach (var source in Enum.GetValues<StartupSource>())
            {
                var entry = Entry() with { Source = source };
                Require(Path.GetDirectoryName(StartupPolicy.BackupPath(entry)) == StartupPolicy.BackupRoot(entry.IsMachine));
                Require(Path.GetFileName(StartupPolicy.BackupPath(entry)) == entry.Id + ".json");
                Require(Path.GetDirectoryName(StartupPolicy.PayloadPath(entry)) == StartupPolicy.BackupRoot(entry.IsMachine));
            }
        });
        yield return Check("启动项真实枚举只读且标识无重复，异常来源保留警告", () =>
        {
            var snapshot = new StartupDiscoveryService().Discover();
            Require(snapshot.Entries.Select(entry => entry.Id).Distinct().Count() == snapshot.Entries.Count);
            Require(snapshot.Entries.All(entry => entry.Name.Length > 0 && entry.Location.Length > 0));
            Console.WriteLine($"  Read-only startup inventory: {snapshot.Entries.Count} entries, {snapshot.Warnings.Count} warnings.");
        });
    }

    private static StartupEntry Entry() => new() { Source = StartupSource.UserRun, Name = "Example", Command = @"C:\Synthetic\Example.exe --startup" };
    private static (string Name, Func<Task> Run) Check(string name, Action action) => (name, () => { action(); return Task.CompletedTask; });
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("启动项检查失败。"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("未拒绝无效启动项操作。");
    }
}
