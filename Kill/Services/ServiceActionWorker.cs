using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public static partial class ServiceActionWorker
{
    private const string ServicesRegistryPath = @"SYSTEM\CurrentControlSet\Services";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task ExecuteAsync(string requestPath)
    {
        ServiceActionRequest? request = null;
        try
        {
            ValidateTemporaryPath(requestPath);
            request = JsonSerializer.Deserialize<ServiceActionRequest>(
                          await File.ReadAllTextAsync(requestPath).ConfigureAwait(false), JsonOptions)
                      ?? throw new InvalidDataException("服务操作请求无效。");
            ValidateServiceName(request.ServiceName);
            ValidateTemporaryPath(request.ResponsePath);
            var response = await ExecuteActionAsync(request).ConfigureAwait(false);
            await WriteResponseAsync(request.ResponsePath, response).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (request is not null && IsTemporaryPath(request.ResponsePath))
            {
                await WriteResponseAsync(request.ResponsePath, new ServiceActionResponse
                {
                    Success = false,
                    Message = $"服务操作失败：{exception.Message}"
                }).ConfigureAwait(false);
            }
        }
    }

    private static async Task<ServiceActionResponse> ExecuteActionAsync(ServiceActionRequest request)
    {
        var blacklist = ServiceBlacklistStore.Read().ToList();
        var blacklistEntry = blacklist.FirstOrDefault(entry =>
            entry.ServiceName.Equals(request.ServiceName, StringComparison.OrdinalIgnoreCase));

        if (request.Action == ServiceActionKind.RemoveFromBlacklist)
        {
            if (blacklistEntry is null)
                return new ServiceActionResponse { Success = true, Message = "该服务不在黑名单中。" };
            var existing = ReadService(request.ServiceName);
            if (existing is not null && !existing.IsProtected)
            {
                await ConfigureStartModeAsync(request.ServiceName, blacklistEntry.OriginalStartValue,
                    blacklistEntry.OriginalDelayedAutoStart).ConfigureAwait(false);
            }
            blacklist.Remove(blacklistEntry);
            await ServiceBlacklistStore.WriteAsync(blacklist).ConfigureAwait(false);
            return new ServiceActionResponse
            {
                Success = true,
                Message = existing switch
                {
                    null => "服务已不存在；已移除 Kill 中的黑名单记录。",
                    { IsProtected: true } => "已移出黑名单；当前服务被判定为系统保护，未修改其启动模式。",
                    _ => "已移出黑名单并恢复原启动模式，服务不会自动启动。"
                }
            };
        }

        var service = ReadService(request.ServiceName) ?? throw new InvalidOperationException("服务已不存在。");
        if (service.IsProtected) throw new InvalidOperationException("安全策略拒绝修改系统保护服务。");

        switch (request.Action)
        {
            case ServiceActionKind.Disable:
                await ConfigureStartModeAsync(service.Name, 4, false).ConfigureAwait(false);
                var disabledStopped = await TryStopServiceAsync(service.Name).ConfigureAwait(false);
                return new ServiceActionResponse
                {
                    Success = true,
                    Message = disabledStopped
                        ? $"已停止并禁用服务“{service.DisplayName}”。"
                        : $"已禁用服务“{service.DisplayName}”，但 Windows 未能立即停止它；重启后禁用状态生效。"
                };

            case ServiceActionKind.AddToBlacklist:
                await ConfigureStartModeAsync(service.Name, 4, false).ConfigureAwait(false);
                if (blacklistEntry is null)
                {
                    blacklist.Add(new ServiceBlacklistEntry
                    {
                        ServiceName = service.Name,
                        DisplayName = service.DisplayName,
                        OriginalStartValue = service.StartValue,
                        OriginalDelayedAutoStart = service.DelayedAutoStart,
                        CreatedAt = DateTimeOffset.Now
                    });
                }
                await ServiceBlacklistStore.WriteAsync(blacklist).ConfigureAwait(false);
                var blacklistedStopped = await TryStopServiceAsync(service.Name).ConfigureAwait(false);
                return new ServiceActionResponse
                {
                    Success = true,
                    Message = blacklistedStopped
                        ? $"已停止、禁用并将“{service.DisplayName}”列入黑名单。"
                        : $"已禁用并将“{service.DisplayName}”列入黑名单，但 Windows 未能立即停止它；重启后禁用状态生效。"
                };

            case ServiceActionKind.Delete:
                var backupPath = await ExportServiceRegistryAsync(service.Name).ConfigureAwait(false);
                var deletedStopped = await TryStopServiceAsync(service.Name).ConfigureAwait(false);
                await RequireScSuccessAsync("delete", service.Name).ConfigureAwait(false);
                blacklist.RemoveAll(entry => entry.ServiceName.Equals(service.Name, StringComparison.OrdinalIgnoreCase));
                await ServiceBlacklistStore.WriteAsync(blacklist).ConfigureAwait(false);
                return new ServiceActionResponse
                {
                    Success = true,
                    Message = deletedStopped
                        ? $"已删除服务“{service.DisplayName}”。"
                        : $"已提交删除服务“{service.DisplayName}”。它仍在运行，Windows 会在宿主进程退出或系统重启后完成删除。",
                    BackupPath = backupPath
                };

            default:
                throw new InvalidOperationException("未知的服务操作。");
        }
    }

    private static ServiceSnapshot? ReadService(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRegistryPath}\{serviceName}");
        if (key is null) return null;
        var imageCommand = key.GetValue("ImagePath")?.ToString() ?? "";
        var executable = ResolveExecutable(imageCommand);
        using var parameters = key.OpenSubKey("Parameters");
        var serviceDll = ServiceSafetyPolicy.ExpandServicePath(parameters?.GetValue("ServiceDll")?.ToString() ?? "");
        var company = ReadCompanyName(!string.IsNullOrWhiteSpace(serviceDll) ? serviceDll : executable);
        var typeValue = ReadInt(key, "Type");
        var typeLabel = (typeValue & 0x3) != 0 ? "Driver" : "Win32 Service";
        var name = serviceName;
        var displayName = key.GetValue("DisplayName")?.ToString() ?? name;
        return new ServiceSnapshot
        {
            Name = name,
            DisplayName = displayName,
            StartValue = ReadInt(key, "Start"),
            DelayedAutoStart = ReadInt(key, "DelayedAutoStart") == 1,
            IsProtected = ServiceSafetyPolicy.IsProtected(name, executable, serviceDll, typeLabel, company)
        };
    }

    private static async Task<bool> TryStopServiceAsync(string serviceName)
    {
        if (await IsStoppedAsync(serviceName).ConfigureAwait(false)) return true;
        var stop = await RunScAsync("stop", serviceName).ConfigureAwait(false);
        if (stop.ExitCode != 0 && !await IsStoppedAsync(serviceName).ConfigureAwait(false))
            return false;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await IsStoppedAsync(serviceName).ConfigureAwait(false)) return true;
            await Task.Delay(500).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> IsStoppedAsync(string serviceName)
    {
        var query = await RunScAsync("queryex", serviceName).ConfigureAwait(false);
        return query.ExitCode == 0 && StoppedStateRegex().IsMatch(query.Output);
    }

    private static async Task ConfigureStartModeAsync(string serviceName, int startValue, bool delayed)
    {
        var mode = startValue switch
        {
            0 => "boot",
            1 => "system",
            2 when delayed => "delayed-auto",
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => "demand"
        };
        await RequireScSuccessAsync("config", serviceName, "start=", mode).ConfigureAwait(false);
    }

    private static async Task<string> ExportServiceRegistryAsync(string serviceName)
    {
        Directory.CreateDirectory(KillPaths.ServiceBackupRoot);
        var safeName = string.Concat(serviceName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(KillPaths.ServiceBackupRoot, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.reg");
        var registryTarget = $@"HKLM\{ServicesRegistryPath}\{serviceName}";
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("export");
        startInfo.ArgumentList.Add(registryTarget);
        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add("/y");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动服务配置备份。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(destination))
            throw new InvalidOperationException($"服务配置备份失败。{output} {error}".Trim());
        return destination;
    }

    private static async Task RequireScSuccessAsync(params string[] arguments)
    {
        var result = await RunScAsync(arguments).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Windows 服务控制命令失败。{result.Output}".Trim());
    }

    private static async Task<(int ExitCode, string Output)> RunScAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Windows 服务控制器。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return (process.ExitCode, $"{output} {error}".Trim());
    }

    private static string ResolveExecutable(string command)
    {
        try { return ServiceSafetyPolicy.ExpandServicePath(WindowsCommandLine.ResolveExecutable(command).Executable); }
        catch { return ""; }
    }

    private static string ReadCompanyName(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName ?? "" : ""; }
        catch { return ""; }
    }

    private static int ReadInt(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int value => value,
        string value when int.TryParse(value, out var parsed) => parsed,
        _ => 0
    };

    private static void ValidateServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 256 ||
            serviceName.Contains('\\') || serviceName.Contains('/') || serviceName.Contains('\0'))
            throw new InvalidOperationException("服务名称无效。");
    }

    private static void ValidateTemporaryPath(string path)
    {
        if (!IsTemporaryPath(path)) throw new InvalidOperationException("服务操作文件不在 Kill 临时目录中。");
    }

    private static bool IsTemporaryPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(KillPaths.TempRoot));
            return directory?.Equals(tempRoot, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WriteResponseAsync(string path, ServiceActionResponse response)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
    }

    [GeneratedRegex(@"STATE\s*:\s*1\b", RegexOptions.IgnoreCase)]
    private static partial Regex StoppedStateRegex();

    private sealed class ServiceSnapshot
    {
        public required string Name { get; init; }
        public required string DisplayName { get; init; }
        public int StartValue { get; init; }
        public bool DelayedAutoStart { get; init; }
        public bool IsProtected { get; init; }
    }
}
