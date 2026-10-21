using System.Security.Cryptography;
using System.Text.Json;
using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public sealed class StartupDiscoveryService
{
    public StartupSnapshot Discover()
    {
        var entries = new List<StartupEntry>();
        var warnings = new List<string>();
        foreach (var source in Enum.GetValues<StartupSource>())
        {
            if (source == StartupSource.MachineRun32 && !Environment.Is64BitOperatingSystem) continue;
            try
            {
                if (source is StartupSource.UserFolder or StartupSource.CommonFolder)
                {
                    string folder = StartupPolicy.Folder(source);
                    if (!Directory.Exists(folder)) continue;
                    foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(path);
                        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        Read(source, name);
                    }
                }
                else
                {
                    using var hive = StartupPolicy.OpenHive(source);
                    using var key = hive.OpenSubKey(StartupPolicy.RunKey);
                    if (key is null) continue;
                    foreach (string name in key.GetValueNames()) Read(source, name);
                }
            }
            catch (Exception exception) { warnings.Add($"{source}：{exception.Message}"); }
        }

        foreach (bool machine in new[] { false, true })
        {
            string root = StartupPolicy.BackupRoot(machine);
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var backup = ReadBackup(path, machine);
                        int index = entries.FindIndex(item => item.Id == backup.Entry.Id);
                        if (index >= 0) entries[index] = entries[index] with { HasConflict = true };
                        else
                        {
                            bool conflict = backup.Entry.IsFolder && (!File.Exists(StartupPolicy.PayloadPath(backup.Entry)) || Directory.Exists(backup.Entry.Location));
                            entries.Add(backup.Entry with { IsDisabled = true, HasConflict = conflict });
                        }
                    }
                    catch (Exception exception) { warnings.Add($"备份 {Path.GetFileName(path)}：{exception.Message}"); }
                }
            }
            catch (Exception exception) { warnings.Add($"备份目录：{exception.Message}"); }
        }
        return new(entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), warnings);

        void Read(StartupSource source, string name)
        {
            try { if (ReadLive(source, name) is { } entry) entries.Add(entry); }
            catch (Exception exception) { warnings.Add($"{name}：{exception.Message}"); }
        }
    }

    internal static StartupEntry? ReadLive(StartupSource source, string name)
    {
        var entry = new StartupEntry { Source = source, Name = name };
        StartupPolicy.Validate(entry);
        if (entry.IsFolder)
        {
            if (!File.Exists(entry.Location)) return null;
            string reason = "";
            string hash = "";
            try
            {
                WorkerInputPolicy.ValidateNoReparsePoints(entry.Location);
                if ((File.GetAttributes(entry.Location) & (FileAttributes.System | FileAttributes.Hidden)) != 0)
                    reason = "系统或隐藏启动文件，仅供查看。";
                else
                {
                    using var stream = new FileStream(entry.Location, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > 64 * 1024 * 1024) reason = "启动文件超过 64 MB，仅供查看。";
                    else hash = Convert.ToHexString(SHA256.HashData(stream));
                }
            }
            catch (Exception exception) { reason = "无法安全读取启动文件：" + exception.Message; }
            return entry with { Command = entry.Location, FileHash = hash, ProtectionReason = reason, Approval = ReadApproval(entry) };
        }
        using var hive = StartupPolicy.OpenHive(source);
        using var key = hive.OpenSubKey(StartupPolicy.RunKey);
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var kind = key.GetValueKind(name);
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string command = value as string ?? "（非字符串注册值）";
        return entry with
        {
            Command = command,
            ValueKind = kind,
            Approval = ReadApproval(entry),
            ProtectionReason = kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? StartupPolicy.Protection(command) : "不支持的注册值类型，仅供查看。"
        };
    }

    internal static StartupBackup ReadBackup(string path, bool machine)
    {
        WorkerInputPolicy.ValidateNoReparsePoints(path);
        if (machine)
        {
            StartupActionCoordinator.ValidateMachineBackupSecurity(StartupPolicy.BackupRoot(true));
            StartupActionCoordinator.ValidateMachineBackupSecurity(path);
        }
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("备份文件过大。");
        var backup = JsonSerializer.Deserialize<StartupBackup>(File.ReadAllText(path)) ?? throw new InvalidDataException("备份格式无效。");
        StartupPolicy.Validate(backup.Entry);
        if (backup.Entry.IsMachine != machine || !Path.GetFullPath(path).Equals(StartupPolicy.BackupPath(backup.Entry), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("备份标识与来源不匹配。");
        return backup;
    }

    // StartupApproved is read-only here. Unknown layouts are never treated as enabled.
    internal static StartupApproval InterpretApproval(byte[]? bytes) => bytes is null ? StartupApproval.Registered :
        bytes.Length != 12 ? StartupApproval.Unknown : BitConverter.ToUInt32(bytes) switch
        {
            2 or 6 => StartupApproval.Registered,
            3 or 7 => StartupApproval.WindowsDisabled,
            _ => StartupApproval.Unknown
        };

    private static StartupApproval ReadApproval(StartupEntry entry)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(entry.IsMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            string group = entry.IsFolder ? "StartupFolder" : entry.Source == StartupSource.MachineRun32 ? "Run32" : "Run";
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + group);
            object? value = key?.GetValue(entry.Name);
            return value is null ? StartupApproval.Registered : value is byte[] bytes ? InterpretApproval(bytes) : StartupApproval.Unknown;
        }
        catch { return StartupApproval.Unknown; }
    }
}
