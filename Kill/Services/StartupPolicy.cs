using System.Security.Cryptography;
using System.Text;
using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public static class StartupPolicy
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string Identity(StartupSource source, string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{(int)source}:{name.ToUpperInvariant()}")));

    public static void Validate(StartupEntry entry)
    {
        if (!Enum.IsDefined(entry.Source) || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Contains('\0') || entry.Name.Length > 16383)
            throw new InvalidDataException("启动项来源或名称无效。");
        if (entry.IsFolder && (entry.Name != Path.GetFileName(entry.Name) || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.Name is "." or ".."
            || entry.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("启动文件必须直接位于 Windows 启动文件夹中。");
        if (!entry.IsFolder && entry.ValueKind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidDataException("只支持字符串类型的 Run 启动项。");
        if (entry.Source == StartupSource.MachineRun32 && !Environment.Is64BitOperatingSystem)
            throw new InvalidDataException("当前系统没有独立的 32 位注册表视图。");
    }

    public static string Folder(StartupSource source) => source switch
    {
        StartupSource.UserFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        StartupSource.CommonFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        _ => throw new InvalidDataException("不是启动文件夹来源。")
    };

    public static string Location(StartupSource source, string name) => source is StartupSource.UserFolder or StartupSource.CommonFolder
        ? Path.Combine(Folder(source), name)
        : $"{(source == StartupSource.UserRun ? "HKCU" : "HKLM")}\\{(source == StartupSource.MachineRun32 ? @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run" : RunKey)}\\{name}";

    internal static RegistryKey OpenHive(StartupSource source) => RegistryKey.OpenBaseKey(
        source == StartupSource.UserRun ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
        source == StartupSource.MachineRun32 ? RegistryView.Registry32 : Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);

    internal static string BackupRoot(bool machine) => machine ? KillPaths.CommonStartupBackupRoot : KillPaths.StartupBackupRoot;
    internal static string BackupPath(StartupEntry entry) => Path.Combine(BackupRoot(entry.IsMachine), entry.Id + ".json");
    internal static string PayloadPath(StartupEntry entry) => Path.Combine(BackupRoot(entry.IsMachine), entry.Id + ".bin");

    internal static bool SameContent(StartupEntry expected, StartupEntry actual) => expected.Id == actual.Id &&
        (expected.IsFolder ? expected.FileHash == actual.FileHash : expected.Command == actual.Command && expected.ValueKind == actual.ValueKind);

    internal static void ValidateDisable(StartupEntry expected, StartupEntry? current)
    {
        if (current is null || !SameContent(expected, current)) throw new InvalidOperationException("启动项已更改或消失，请刷新后重试。");
        if (!current.CanDisable) throw new InvalidOperationException(current.ActionNote);
    }

    internal static void ValidateRestore(StartupEntry expected, StartupEntry backup, bool originalExists)
    {
        Validate(backup);
        if (!SameContent(expected, backup)) throw new InvalidDataException("备份内容已更改，请刷新后重试。");
        if (originalExists) throw new InvalidOperationException("原位置已有同名项目，未覆盖。");
    }

    internal static string ResolveExecutable(string command)
    {
        try
        {
            string path = WindowsCommandLine.ResolveExecutable(command).Executable;
            return Path.IsPathFullyQualified(path) && File.Exists(path) ? Path.GetFullPath(path) : "";
        }
        catch { return ""; }
    }

    internal static string Protection(string command)
    {
        string path = ResolveExecutable(command);
        if (path.Length == 0) return "无法确认程序路径，仅供查看。";
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return "Windows 系统组件，仅供查看。";
        return "";
    }
}
