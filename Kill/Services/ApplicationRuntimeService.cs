using Kill.Models;
using ProcessSentinel.Core;

namespace Kill.Services;

public sealed class ApplicationRuntimeService
{
    private static readonly HashSet<string> SharedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec.exe", "rundll32.exe", "regsvr32.exe", "svchost.exe", "dllhost.exe",
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "dotnet.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "node.exe",
        "explorer.exe", "conhost.exe", "ApplicationFrameHost.exe",
        "uninstall.exe", "uninstaller.exe", "unins000.exe", "setup.exe"
    };

    public IReadOnlyDictionary<InstalledApplication, ApplicationRuntimeStatus> Capture(
        IReadOnlyList<InstalledApplication> applications, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = ProcessCatalog.Snapshot().Where(process => process.Id > 4).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Match(applications, processes.Select(process => new ApplicationProcess(process.Id, process.Path)),
            DateTimeOffset.UtcNow, processes.Count(process => string.IsNullOrWhiteSpace(process.Path)));
    }

    internal static IReadOnlyDictionary<InstalledApplication, ApplicationRuntimeStatus> Match(
        IReadOnlyList<InstalledApplication> applications, IEnumerable<ApplicationProcess> processes,
        DateTimeOffset capturedAtUtc, int unreadableProcessCount = 0)
    {
        var evidence = applications.Select(application => new Evidence(application,
            GetInstallDirectory(application), GetExecutable(application.DisplayIcon))).ToArray();
        var matches = applications.ToDictionary(application => application, _ => new List<ApplicationProcess>());
        var ambiguous = new HashSet<InstalledApplication>();

        foreach (var process in processes.DistinctBy(process => process.Id))
        {
            var path = NormalizePath(process.ExecutablePath);
            if (path is null || SharedExecutables.Contains(Path.GetFileName(path))) continue;
            var candidates = evidence.Where(item => string.Equals(item.Executable, path,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0)
            {
                candidates = evidence.Where(item => item.Directory is not null &&
                    path.StartsWith(item.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length > 0)
                {
                    int longest = candidates.Max(item => item.Directory!.Length);
                    candidates = candidates.Where(item => item.Directory!.Length == longest).ToArray();
                }
            }
            if (candidates.Length == 1) matches[candidates[0].Application].Add(process with { ExecutablePath = path });
            else foreach (var candidate in candidates) ambiguous.Add(candidate.Application);
        }

        var results = new Dictionary<InstalledApplication, ApplicationRuntimeStatus>();
        foreach (var item in evidence)
        {
            var running = matches[item.Application].OrderBy(process => process.Id).ToArray();
            var state = running.Length > 0 ? ApplicationRunState.Running :
                ambiguous.Contains(item.Application) || (item.Directory is null && item.Executable is null)
                    ? ApplicationRunState.Unknown : ApplicationRunState.NotDetected;
            string description = state switch
            {
                ApplicationRunState.Running => $"检测到 {running.Length} 个路径可关联的进程（含后台进程）。",
                ApplicationRunState.Unknown when ambiguous.Contains(item.Application) => "运行进程位于多个应用共用的路径，无法确认具体归属。",
                ApplicationRunState.Unknown => "缺少可用于识别进程的安装位置或可执行文件信息。",
                _ => "未检测到路径可关联的运行进程。"
            };
            if (unreadableProcessCount > 0) description += $" 有 {unreadableProcessCount} 个进程的路径无法读取，结果可能不完整。";
            results[item.Application] = new ApplicationRuntimeStatus(state, running, description, capturedAtUtc);
        }
        return results;
    }

    private static string? GetInstallDirectory(InstalledApplication application)
    {
        var directory = NormalizePath(application.InstallLocation);
        if (directory is null || SafetyPolicy.IsProtectedInstallLocation(directory)) return null;
        var sharedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")
        };
        return sharedRoots.Any(root => !string.IsNullOrEmpty(root) && SafetyPolicy.PathsEqual(directory, root)) ? null : directory;
    }

    private static string? GetExecutable(string displayIcon)
    {
        var path = InstallDirectoryIdentificationService.ResolveDisplayIconPath(displayIcon);
        if (path is null || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            SharedExecutables.Contains(Path.GetFileName(path))) return null;
        return path;
    }

    private static string? NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded)) return null;
        try { return SafetyPolicy.NormalizePath(expanded); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private sealed record Evidence(InstalledApplication Application, string? Directory, string? Executable);
}
