using System.Text.RegularExpressions;
using Kill.Models;

namespace Kill.Services;

internal static partial class WorkerInputPolicy
{
    internal static void ValidateTemporaryPath(string path) => ValidateDirectChild(path, KillPaths.TempRoot, ".json");

    internal static void ValidateHistoryPath(string path) => ValidateDirectChild(path, KillPaths.HistoryRoot, ".json");

    internal static void ValidateManifestPath(string path, string operationId)
    {
        ValidateOperationId(operationId);
        ValidateDirectChild(path, KillPaths.HistoryRoot, ".json");
        if (!Path.GetFileNameWithoutExtension(path).Equals(operationId, StringComparison.Ordinal))
            throw new InvalidDataException("隔离记录与操作标识不匹配。");
    }

    internal static void ValidateOperationId(string id)
    {
        if (id is null || !OperationIdPattern().IsMatch(id)) throw new InvalidDataException("操作标识无效。");
    }

    internal static void ValidateItem(CleanupCandidateDto item)
    {
        if (!Guid.TryParseExact(item.Id, "N", out _) || !Enum.IsDefined(item.Kind))
            throw new InvalidDataException("清理项目标识或类型无效。");
        if (item.Kind != CleanupItemKind.RegistryKey) ValidateNoReparsePoints(item.Target);
    }

    internal static void ValidateRestoreEntry(string operationId, OperationEntry entry)
    {
        ValidateOperationId(operationId);
        if (!Enum.IsDefined(entry.Kind)) throw new InvalidDataException("恢复项目类型无效。");
        string expectedRoot;
        if (entry.Kind == CleanupItemKind.RegistryKey)
        {
            expectedRoot = Path.Combine(KillPaths.QuarantineRoot, operationId, "Registry");
            ValidateDirectChild(entry.BackupTarget, expectedRoot, ".reg");
        }
        else
        {
            if (!SafetyPolicy.IsPathSafeToQuarantine(entry.OriginalTarget))
                throw new InvalidDataException("安全策略拒绝此恢复位置。");
            ValidateNoReparsePoints(entry.OriginalTarget);
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(entry.OriginalTarget))!;
            var quarantineRoot = sourceRoot.Equals(Path.GetPathRoot(KillPaths.QuarantineRoot), StringComparison.OrdinalIgnoreCase)
                ? KillPaths.QuarantineRoot : Path.Combine(sourceRoot, "Kill Quarantine");
            expectedRoot = Path.Combine(quarantineRoot, operationId, "Files");
            ValidateDirectChild(entry.BackupTarget, expectedRoot);
        }
        string fileName = Path.GetFileName(entry.BackupTarget);
        string id = entry.Kind == CleanupItemKind.Directory ? fileName : Path.GetFileNameWithoutExtension(fileName);
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("隔离副本标识无效。");
    }

    internal static void ValidateRegistryBackup(string source, string target)
    {
        string expected = target.Replace("HKCU\\", "HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)
            .Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase).TrimEnd('\\');
        bool hasKey = false;
        foreach (string line in File.ReadLines(source))
        {
            string value = line.Trim();
            if (!value.StartsWith('[')) continue;
            if (!value.EndsWith(']')) throw new InvalidDataException("注册表备份格式无效。");
            string key = value[1..^1];
            if (!key.Equals(expected, StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith(expected + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("注册表备份包含范围之外的键。");
            hasKey = true;
        }
        if (!hasKey) throw new InvalidDataException("注册表备份没有可恢复的键。");
    }

    private static void ValidateDirectChild(string path, string directory, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("工作进程需要完整路径。");
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase)
            || (extension is not null && !Path.GetExtension(fullPath).Equals(extension, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("工作进程路径超出允许目录。");
        ValidateNoReparsePoints(fullPath);
    }

    internal static void ValidateNoReparsePoints(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("不允许网络、设备或备用数据流路径。");
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("工作进程不允许符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}-[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdPattern();
}
