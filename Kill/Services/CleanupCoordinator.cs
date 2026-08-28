using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Kill.Models;

namespace Kill.Services;

public sealed class CleanupCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public Task<WorkerResponse> CleanAsync(string applicationName, IReadOnlyCollection<CleanupCandidate> candidates) =>
        ExecuteCleanupRequestAsync("--cleanup", applicationName, candidates, "已取消 Windows 权限确认，未清理任何残留。");

    public Task<WorkerResponse> PermanentDeleteAsync(string applicationName, IReadOnlyCollection<CleanupCandidate> candidates) =>
        ExecuteCleanupRequestAsync("--delete", applicationName, candidates, "已取消 Windows 权限确认，未直接清理任何残留。");

    private static async Task<WorkerResponse> ExecuteCleanupRequestAsync(string workerMode, string applicationName,
        IReadOnlyCollection<CleanupCandidate> candidates, string cancelledMessage)
    {
        var operationId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(KillPaths.TempRoot);
        var responsePath = Path.Combine(KillPaths.TempRoot, $"{operationId}-response.json");
        var requestPath = Path.Combine(KillPaths.TempRoot, $"{operationId}-request.json");
        var request = new CleanupRequest
        {
            OperationId = operationId,
            ApplicationName = applicationName,
            ResponsePath = responsePath,
            Items = candidates.Select(candidate => new CleanupCandidateDto
            {
                Id = candidate.Id,
                Kind = candidate.Kind,
                Target = candidate.Target,
                Reason = candidate.Reason,
                Risk = candidate.Risk,
                RegistryViewName = candidate.RegistryViewName
            }).ToList()
        };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions));

        try
        {
            using var process = StartElevatedWorker(workerMode, requestPath);
            await process.WaitForExitAsync();
            return await ReadResponseAsync(responsePath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new WorkerResponse { Success = false, Message = cancelledMessage };
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    public async Task<WorkerResponse> RestoreAsync(string manifestPath)
    {
        Directory.CreateDirectory(KillPaths.TempRoot);
        var responsePath = Path.Combine(KillPaths.TempRoot, $"restore-{Guid.NewGuid():N}.json");
        try
        {
            using var process = StartElevatedWorker("--restore", manifestPath, responsePath);
            await process.WaitForExitAsync();
            return await ReadResponseAsync(responsePath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new WorkerResponse { Success = false, Message = "已取消 Windows 权限确认，未恢复任何项目。" };
        }
        finally
        {
            TryDelete(responsePath);
        }
    }

    public IReadOnlyList<(string Path, OperationManifest Manifest)> GetHistory()
    {
        if (!Directory.Exists(KillPaths.HistoryRoot)) return [];
        var history = new List<(string, OperationManifest)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(KillPaths.HistoryRoot, "*.json"))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<OperationManifest>(File.ReadAllText(path), JsonOptions);
                    if (manifest is not null) history.Add((path, manifest));
                }
                catch { }
            }
        }
        catch { return []; }
        return history.OrderByDescending(item => item.Item2.CreatedAt).ToList();
    }

    private static Process StartElevatedWorker(params string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 Kill 可执行文件。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动安全清理进程。");
    }

    private static async Task<WorkerResponse> ReadResponseAsync(string path)
    {
        if (!File.Exists(path)) return new WorkerResponse { Success = false, Message = "清理进程未返回结果。" };
        return JsonSerializer.Deserialize<WorkerResponse>(await File.ReadAllTextAsync(path), JsonOptions)
               ?? new WorkerResponse { Success = false, Message = "无法读取清理结果。" };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
