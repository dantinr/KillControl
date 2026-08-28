using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Kill.Models;

namespace Kill.Services;

public sealed class ServiceActionCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<ServiceActionResponse> ExecuteAsync(string serviceName, ServiceActionKind action)
    {
        Directory.CreateDirectory(KillPaths.TempRoot);
        var requestId = $"service-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var requestPath = Path.Combine(KillPaths.TempRoot, $"{requestId}-request.json");
        var responsePath = Path.Combine(KillPaths.TempRoot, $"{requestId}-response.json");
        var request = new ServiceActionRequest
        {
            ServiceName = serviceName,
            Action = action,
            ResponsePath = responsePath
        };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions));

        try
        {
            using var process = StartElevatedWorker(requestPath);
            await process.WaitForExitAsync();
            if (!File.Exists(responsePath))
                return new ServiceActionResponse { Success = false, Message = "服务工作进程未返回结果。" };
            return JsonSerializer.Deserialize<ServiceActionResponse>(await File.ReadAllTextAsync(responsePath), JsonOptions)
                   ?? new ServiceActionResponse { Success = false, Message = "无法读取服务操作结果。" };
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new ServiceActionResponse { Success = false, Message = "已取消 Windows 权限确认，未修改服务。" };
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    private static Process StartElevatedWorker(string requestPath)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 Kill 可执行文件。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("--service-action");
        startInfo.ArgumentList.Add(requestPath);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动服务管理进程。");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
