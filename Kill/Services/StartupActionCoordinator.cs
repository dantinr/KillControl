using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public sealed class StartupActionCoordinator
{
    public async Task<StartupActionResult> ExecuteAsync(StartupEntry entry, bool restore)
    {
        StartupPolicy.Validate(entry);
        if (!entry.IsMachine) return await Task.Run(() => ExecuteCore(entry, restore));
        Directory.CreateDirectory(KillPaths.TempRoot);
        string id = "startup-" + Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(KillPaths.TempRoot, id + "-request.json");
        string responsePath = Path.Combine(KillPaths.TempRoot, id + "-response.json");
        WorkerInputPolicy.ValidateTemporaryPath(requestPath);
        WorkerInputPolicy.ValidateTemporaryPath(responsePath);
        var request = new StartupActionRequest(entry, restore, WindowsIdentity.GetCurrent().User!.Value, responsePath);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--startup-action");
            start.ArgumentList.Add(requestPath);
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动启动项管理进程。");
            await worker.WaitForExitAsync();
            return File.Exists(responsePath)
                ? JsonSerializer.Deserialize<StartupActionResult>(await File.ReadAllTextAsync(responsePath)) ?? new(false, "无法读取操作结果。")
                : new(false, "工作进程未返回结果，请刷新列表确认。使用其他管理员账户授权时不会修改当前用户的启动项。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { return new(false, "已取消 Windows 权限确认。"); }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    internal static Task RunWorkerAsync(string requestPath)
    {
        StartupActionRequest? request = null;
        try
        {
            WorkerInputPolicy.ValidateTemporaryPath(requestPath);
            if (new FileInfo(requestPath).Length > 1024 * 1024) throw new InvalidDataException("请求过大。");
            request = JsonSerializer.Deserialize<StartupActionRequest>(File.ReadAllText(requestPath)) ?? throw new InvalidDataException("请求格式无效。");
            WorkerInputPolicy.ValidateTemporaryPath(request.ResponsePath);
            if (request.UserSid != WindowsIdentity.GetCurrent().User!.Value || !request.Entry.IsMachine)
                throw new InvalidDataException("工作进程账户或操作范围不匹配。");
            var result = ExecuteCore(request.Entry, request.Restore);
            File.WriteAllText(request.ResponsePath, JsonSerializer.Serialize(result));
        }
        catch (Exception exception)
        {
            if (request is not null)
            {
                try
                {
                    WorkerInputPolicy.ValidateTemporaryPath(request.ResponsePath);
                    File.WriteAllText(request.ResponsePath, JsonSerializer.Serialize(new StartupActionResult(false, exception.Message)));
                }
                catch { }
            }
        }
        return Task.CompletedTask;
    }

    private static StartupActionResult ExecuteCore(StartupEntry expected, bool restore)
    {
        try
        {
            StartupPolicy.Validate(expected);
            string mutexName = (expected.IsMachine ? @"Global\" : @"Local\") + "KillStartup-" + expected.Id;
            using var mutex = new Mutex(false, mutexName);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new(false, "该启动项正在被另一个窗口处理，请稍后刷新。");
            try
            {
                string root = StartupPolicy.BackupRoot(expected.IsMachine);
                WorkerInputPolicy.ValidateNoReparsePoints(root);
                if (expected.IsMachine) EnsureMachineBackupDirectory(root);
                else Directory.CreateDirectory(root);
                string manifest = StartupPolicy.BackupPath(expected);
                string payload = StartupPolicy.PayloadPath(expected);
                WorkerInputPolicy.ValidateNoReparsePoints(manifest);
                WorkerInputPolicy.ValidateNoReparsePoints(payload);
                var current = StartupDiscoveryService.ReadLive(expected.Source, expected.Name);
                if (restore)
                {
                    var backup = StartupDiscoveryService.ReadBackup(manifest, expected.IsMachine);
                    StartupPolicy.ValidateRestore(expected, backup.Entry, current is not null || (expected.IsFolder && Directory.Exists(expected.Location)));
                    if (expected.IsFolder)
                    {
                        WorkerInputPolicy.ValidateNoReparsePoints(expected.Location);
                        using (var stream = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read))
                            if (Convert.ToHexString(SHA256.HashData(stream)) != backup.Entry.FileHash)
                                throw new InvalidDataException("启动文件备份校验失败，未恢复。");
                        File.Move(payload, expected.Location, false);
                    }
                    else
                    {
                        using var hive = StartupPolicy.OpenHive(expected.Source);
                        using var key = hive.CreateSubKey(StartupPolicy.RunKey, true);
                        if (key.GetValueNames().Contains(expected.Name, StringComparer.OrdinalIgnoreCase))
                            throw new InvalidOperationException("原注册值已存在，未覆盖。");
                        key.SetValue(expected.Name, backup.Entry.Command, backup.Entry.ValueKind);
                        key.Flush();
                    }
                    TryDelete(manifest);
                    return new(true, "已恢复原始启动项，下次登录生效。Windows 自身的禁用设置保持原样。");
                }

                StartupPolicy.ValidateDisable(expected, current);
                if (File.Exists(manifest) || File.Exists(payload)) throw new InvalidOperationException("已有备份，请先检查冲突，未覆盖原备份。");
                // Persist the recovery record before touching the original registration.
                using (var stream = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new StartupBackup(current!, DateTimeOffset.UtcNow));
                    stream.Flush(true);
                }
                try
                {
                    if (expected.IsMachine)
                    {
                        var ownership = new FileSecurity();
                        ownership.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                        new FileInfo(manifest).SetAccessControl(ownership);
                        ValidateMachineBackupSecurity(manifest);
                    }
                    if (expected.IsFolder)
                    {
                        WorkerInputPolicy.ValidateNoReparsePoints(expected.Location);
                        StartupPolicy.ValidateDisable(expected, StartupDiscoveryService.ReadLive(expected.Source, expected.Name));
                        File.Move(expected.Location, payload, false);
                    }
                    else
                    {
                        using var hive = StartupPolicy.OpenHive(expected.Source);
                        using var key = hive.OpenSubKey(StartupPolicy.RunKey, true) ?? throw new InvalidOperationException("启动项已不存在。");
                        StartupPolicy.ValidateDisable(expected, StartupDiscoveryService.ReadLive(expected.Source, expected.Name));
                        key.DeleteValue(expected.Name, true);
                        key.Flush();
                    }
                }
                catch
                {
                    // Remove only an unused recovery record; otherwise preserve it for manual recovery.
                    if (!File.Exists(payload) && StartupDiscoveryService.ReadLive(expected.Source, expected.Name) is { } unchanged && StartupPolicy.SameContent(expected, unchanged))
                        TryDelete(manifest);
                    throw;
                }
                return new(true, "已禁用并保存原始启动项，可在本页恢复。正在运行的程序不受影响。");
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception exception) { return new(false, "启动项操作未完成：" + exception.Message); }
    }

    private static void EnsureMachineBackupDirectory(string root)
    {
        if (Directory.Exists(root))
        {
            ValidateMachineBackupSecurity(root);
            return;
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(root);
        FileSystemAclExtensions.Create(directory, security);
        ValidateMachineBackupSecurity(root);
    }

    internal static void ValidateMachineBackupSecurity(string path)
    {
        WorkerInputPolicy.ValidateNoReparsePoints(path);
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        bool Trusted(IdentityReference sid) => sid.Equals(administrators) || sid.Equals(system);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !Trusted(owner))
            throw new InvalidDataException("所有用户启动项的备份必须由管理员或 SYSTEM 所有。");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 && !Trusted(rule.IdentityReference))
                throw new InvalidDataException("所有用户启动项的备份权限不安全，已拒绝使用。");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}
