using System.Diagnostics;
using System.Text.Json;
using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public static class CleanupWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static Task ExecuteCleanupAsync(string requestPath) => ExecuteCleanupRequestAsync(requestPath, false);

    public static Task ExecutePermanentDeleteAsync(string requestPath) => ExecuteCleanupRequestAsync(requestPath, true);

    private static async Task ExecuteCleanupRequestAsync(string requestPath, bool isPermanent)
    {
        CleanupRequest? request = null;
        bool responseValidated = false;
        try
        {
            WorkerInputPolicy.ValidateTemporaryPath(requestPath);
            request = JsonSerializer.Deserialize<CleanupRequest>(await File.ReadAllTextAsync(requestPath), JsonOptions)
                      ?? throw new InvalidDataException("清理请求无效。");
            WorkerInputPolicy.ValidateTemporaryPath(request.ResponsePath);
            if (SafetyPolicy.PathsEqual(requestPath, request.ResponsePath)) throw new InvalidDataException("请求与响应不能使用同一个文件。");
            responseValidated = true;
            WorkerInputPolicy.ValidateOperationId(request.OperationId);
            if (string.IsNullOrWhiteSpace(request.ApplicationName)) throw new InvalidDataException("缺少应用名称。");
            foreach (var candidate in request.Items) WorkerInputPolicy.ValidateItem(candidate);
            var manifest = new OperationManifest
            {
                OperationId = request.OperationId,
                ApplicationName = request.ApplicationName,
                CreatedAt = DateTimeOffset.Now,
                IsPermanent = isPermanent
            };

            Directory.CreateDirectory(KillPaths.HistoryRoot);
            foreach (var item in request.Items)
            {
                try
                {
                    OperationEntry? entry;
                    if (isPermanent)
                    {
                        entry = item.Kind == CleanupItemKind.RegistryKey
                            ? PermanentlyDeleteRegistry(item, request.ApplicationName)
                            : PermanentlyDeleteFileSystem(item);
                    }
                    else
                    {
                        entry = item.Kind == CleanupItemKind.RegistryKey
                            ? await QuarantineRegistryAsync(request.OperationId, item, request.ApplicationName)
                            : QuarantineFileSystem(request.OperationId, item);
                    }
                    if (entry is not null) manifest.Entries.Add(entry);
                }
                catch (Exception exception)
                {
                    manifest.Errors.Add($"{item.Target}: {exception.Message}");
                }
            }

            var manifestPath = Path.Combine(KillPaths.HistoryRoot, $"{request.OperationId}.json");
            await WriteJsonAsync(manifestPath, manifest);
            await WriteResponseAsync(request.ResponsePath, new WorkerResponse
            {
                Success = manifest.Entries.Count > 0 && manifest.Errors.Count == 0,
                Message = manifest.Entries.Count == 0
                    ? (isPermanent ? "没有项目被直接清理。" : "没有项目被移入隔离区。")
                    : isPermanent
                        ? $"已直接清理 {manifest.Entries.Count} 个项目，此操作不可恢复。"
                        : $"已将 {manifest.Entries.Count} 个项目移入隔离区。",
                ManifestPath = manifestPath,
                Errors = manifest.Errors
            });
        }
        catch (Exception exception)
        {
            if (request is not null && responseValidated)
            {
                await WriteResponseAsync(request.ResponsePath, new WorkerResponse
                {
                    Success = false,
                    Message = $"{(isPermanent ? "直接清理" : "清理")}失败：{exception.Message}",
                    Errors = [exception.Message]
                });
            }
        }
    }

    public static async Task ExecuteRestoreAsync(string manifestPath, string responsePath)
    {
        bool responseValidated = false;
        try
        {
            WorkerInputPolicy.ValidateTemporaryPath(responsePath);
            responseValidated = true;
            WorkerInputPolicy.ValidateHistoryPath(manifestPath);
            var manifest = JsonSerializer.Deserialize<OperationManifest>(await File.ReadAllTextAsync(manifestPath), JsonOptions)
                           ?? throw new InvalidDataException("隔离记录无效。");
            WorkerInputPolicy.ValidateManifestPath(manifestPath, manifest.OperationId);
            if (manifest.IsPermanent)
            {
                await WriteResponseAsync(responsePath, new WorkerResponse
                {
                    Success = false,
                    Message = "直接清理记录没有备份，无法恢复。",
                    ManifestPath = manifestPath
                });
                return;
            }
            var errors = new List<string>();
            var restoredCount = 0;

            foreach (var entry in manifest.Entries.Where(entry => !entry.Restored))
            {
                try
                {
                    WorkerInputPolicy.ValidateRestoreEntry(manifest.OperationId, entry);
                    if (entry.Kind == CleanupItemKind.RegistryKey)
                    {
                        var (hive, relativePath) = ParseAndValidateRegistryTarget(entry.OriginalTarget);
                        using var baseKey = RegistryKey.OpenBaseKey(hive, ParseRegistryView(entry.RegistryViewName));
                        using var existing = baseKey.OpenSubKey(relativePath);
                        if (existing is not null) throw new IOException("注册表原位置已存在，未覆盖现有数据。");
                        WorkerInputPolicy.ValidateRegistryBackup(entry.BackupTarget, entry.OriginalTarget);
                        await ImportRegistryAsync(entry.BackupTarget, entry.RegistryViewName);
                    }
                    else
                        RestoreFileSystem(entry);
                    entry.Restored = true;
                    restoredCount++;
                }
                catch (Exception exception)
                {
                    errors.Add($"{entry.OriginalTarget}: {exception.Message}");
                }
            }

            manifest.Restored = manifest.Entries.All(entry => entry.Restored);
            manifest.Errors.AddRange(errors.Select(error => $"恢复：{error}"));
            await WriteJsonAsync(manifestPath, manifest);
            await WriteResponseAsync(responsePath, new WorkerResponse
            {
                Success = restoredCount > 0 && errors.Count == 0,
                Message = restoredCount == 0 ? "没有可恢复的项目。" : $"已恢复 {restoredCount} 个项目。",
                ManifestPath = manifestPath,
                Errors = errors
            });
        }
        catch (Exception exception)
        {
            if (!responseValidated) return;
            await WriteResponseAsync(responsePath, new WorkerResponse
            {
                Success = false,
                Message = $"恢复失败：{exception.Message}",
                Errors = [exception.Message]
            });
        }
    }

    private static OperationEntry? QuarantineFileSystem(string operationId, CleanupCandidateDto item)
    {
        if (!SafetyPolicy.IsPathSafeToQuarantine(item.Target))
            throw new InvalidOperationException("安全策略拒绝了此路径。");

        var isDirectory = Directory.Exists(item.Target);
        var isFile = File.Exists(item.Target);
        if (!isDirectory && !isFile) return null;
        if (isDirectory != (item.Kind == CleanupItemKind.Directory))
            throw new InvalidOperationException("目标类型已经变化。");

        var destinationRoot = GetSameVolumeQuarantineRoot(item.Target, operationId);
        var suffix = isFile ? Path.GetExtension(item.Target) : "";
        var destination = Path.Combine(destinationRoot, "Files", item.Id + suffix);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (isDirectory) Directory.Move(item.Target, destination);
        else File.Move(item.Target, destination);

        return new OperationEntry
        {
            Kind = item.Kind,
            OriginalTarget = item.Target,
            BackupTarget = destination
        };
    }

    private static async Task<OperationEntry?> QuarantineRegistryAsync(string operationId, CleanupCandidateDto item, string applicationName)
    {
        var (hive, relativePath) = ParseAndValidateRegistryTarget(item.Target);
        var view = ParseRegistryView(item.RegistryViewName);
        ValidateRegistryOwnership(item.Target, applicationName, hive, relativePath, view);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var existing = baseKey.OpenSubKey(relativePath);
        if (existing is null) return null;

        var backupDirectory = Path.Combine(KillPaths.QuarantineRoot, operationId, "Registry");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"{item.Id}.reg");
        await ExportRegistryAsync(item.Target, backupPath, view);
        baseKey.DeleteSubKeyTree(relativePath, false);

        return new OperationEntry
        {
            Kind = CleanupItemKind.RegistryKey,
            OriginalTarget = item.Target,
            BackupTarget = backupPath,
            RegistryViewName = view.ToString()
        };
    }

    private static OperationEntry? PermanentlyDeleteFileSystem(CleanupCandidateDto item)
    {
        if (!SafetyPolicy.IsPathSafeToQuarantine(item.Target))
            throw new InvalidOperationException("安全策略拒绝了此路径。");

        var isDirectory = Directory.Exists(item.Target);
        var isFile = File.Exists(item.Target);
        if (!isDirectory && !isFile) return null;
        if (isDirectory != (item.Kind == CleanupItemKind.Directory))
            throw new InvalidOperationException("目标类型已经变化。");

        if (isDirectory) Directory.Delete(item.Target, true);
        else File.Delete(item.Target);

        return new OperationEntry
        {
            Kind = item.Kind,
            OriginalTarget = item.Target,
            BackupTarget = ""
        };
    }

    private static OperationEntry? PermanentlyDeleteRegistry(CleanupCandidateDto item, string applicationName)
    {
        var (hive, relativePath) = ParseAndValidateRegistryTarget(item.Target);
        var view = ParseRegistryView(item.RegistryViewName);
        ValidateRegistryOwnership(item.Target, applicationName, hive, relativePath, view);
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using (var existing = baseKey.OpenSubKey(relativePath))
        {
            if (existing is null) return null;
        }
        baseKey.DeleteSubKeyTree(relativePath, false);

        return new OperationEntry
        {
            Kind = CleanupItemKind.RegistryKey,
            OriginalTarget = item.Target,
            BackupTarget = "",
            RegistryViewName = view.ToString()
        };
    }

    private static string GetSameVolumeQuarantineRoot(string sourcePath, string operationId)
    {
        var sourceRoot = Path.GetPathRoot(SafetyPolicy.NormalizePath(sourcePath))
                         ?? throw new InvalidOperationException("无法确定目标所在磁盘。");
        var commonRoot = Path.GetPathRoot(KillPaths.QuarantineRoot);
        return string.Equals(sourceRoot, commonRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(KillPaths.QuarantineRoot, operationId)
            : Path.Combine(sourceRoot, "Kill Quarantine", operationId);
    }

    private static void RestoreFileSystem(OperationEntry entry)
    {
        if (Directory.Exists(entry.OriginalTarget) || File.Exists(entry.OriginalTarget))
            throw new IOException("原位置已有同名项目，未覆盖现有数据。");
        if (!Directory.Exists(entry.BackupTarget) && !File.Exists(entry.BackupTarget))
            throw new FileNotFoundException("隔离副本不存在。", entry.BackupTarget);

        var parent = Path.GetDirectoryName(entry.OriginalTarget);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        if (entry.Kind == CleanupItemKind.Directory) Directory.Move(entry.BackupTarget, entry.OriginalTarget);
        else File.Move(entry.BackupTarget, entry.OriginalTarget);
    }

    internal static (RegistryHive Hive, string RelativePath) ParseAndValidateRegistryTarget(string target)
    {
        var normalized = target.Replace('/', '\\').Trim().TrimEnd('\\');
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[1].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("注册表目标不在允许的 SOFTWARE 范围内。");

        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => throw new InvalidOperationException("不允许操作此注册表配置单元。")
        };
        if (parts[2].Equals("Classes", StringComparison.OrdinalIgnoreCase)
            || parts[2].Equals("Policies", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安全策略保护共享注册表配置。");
        if (parts.Length == 3 && (parts[2].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            || parts[2].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("安全策略保护共享注册表根键。");
        string relative = string.Join('\\', parts.Skip(1));
        if (relative.StartsWith(@"SOFTWARE\Microsoft\Windows", StringComparison.OrdinalIgnoreCase)
            && !relative.StartsWith(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安全策略保护 Windows 注册表配置。");
        return (hive, string.Join('\\', parts.Skip(1)));
    }

    private static void ValidateRegistryOwnership(string target, string applicationName, RegistryHive hive,
        string relativePath, RegistryView view)
    {
        string[] parts = relativePath.Split('\\');
        if (parts.Length is 2 or 3 && SafetyPolicy.IsStrongNameMatch(parts[^1], applicationName)) return;
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        if (relativePath.StartsWith(uninstall, StringComparison.OrdinalIgnoreCase)
            && !relativePath[uninstall.Length..].Contains('\\'))
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(relativePath);
            if (key is null || SafetyPolicy.IsStrongNameMatch(key.GetValue("DisplayName") as string ?? "", applicationName)) return;
        }
        throw new InvalidOperationException("无法确认此注册表项属于所选应用：" + target);
    }

    private static RegistryView ParseRegistryView(string name) =>
        Enum.TryParse<RegistryView>(name, out var view) && Enum.IsDefined(view)
            ? view : throw new InvalidDataException("注册表视图无效。");

    private static async Task ExportRegistryAsync(string target, string destination, RegistryView view)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("export");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add("/y");
        startInfo.ArgumentList.Add(view == RegistryView.Registry32 ? "/reg:32" : "/reg:64");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动注册表备份程序。");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(destination))
            throw new InvalidOperationException($"注册表备份失败。{error}".Trim());
    }

    private static async Task ImportRegistryAsync(string source, string viewName)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("注册表备份不存在。", source);
        var view = ParseRegistryView(viewName);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("import");
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add(view == RegistryView.Registry32 ? "/reg:32" : "/reg:64");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动注册表恢复程序。");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"注册表恢复失败。{error}".Trim());
    }

    private static Task WriteResponseAsync(string path, WorkerResponse response) => WriteJsonAsync(path, response);

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}
