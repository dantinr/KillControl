using System.ComponentModel;
using System.Diagnostics;
using Kill.Models;

namespace Kill.Services;

public sealed record UninstallResult(bool Started, bool Cancelled, string Message);

public sealed class UninstallService
{
    public async Task<UninstallResult> UninstallAsync(InstalledApplication application, CancellationToken cancellationToken = default)
    {
        if (!application.CanUninstall) return new(false, false, "此应用没有提供可用的卸载入口。");

        try
        {
            using var process = application.Kind == ApplicationKind.Store
                ? StartStoreUninstall(application)
                : StartDesktopUninstall(application);

            if (process is null) return new(false, false, "Windows 未能启动卸载程序。");
            await process.WaitForExitAsync(cancellationToken);
            return new(true, false, process.ExitCode switch
            {
                0 => "官方卸载程序已结束。",
                1641 or 3010 => "官方卸载已完成，需要重新启动 Windows 才能完成全部更改。",
                1602 => "已在官方卸载程序中取消操作。",
                _ => $"卸载程序已结束，返回代码 {process.ExitCode}。请确认应用是否已移除。"
            });
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new(false, true, "已取消 Windows 权限确认，未执行卸载。");
        }
        catch (Exception exception)
        {
            return new(false, false, $"无法启动卸载：{exception.Message}");
        }
    }

    private static Process? StartDesktopUninstall(InstalledApplication application)
    {
        var command = !string.IsNullOrWhiteSpace(application.UninstallCommand)
            ? application.UninstallCommand
            : application.QuietUninstallCommand;
        var resolved = WindowsCommandLine.ResolveExecutable(command);
        var arguments = resolved.Arguments.ToList();
        if (Path.GetFileName(resolved.Executable).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(resolved.Executable).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            ConvertMsiMaintenanceToUninstall(arguments);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = resolved.Executable,
            UseShellExecute = true,
            Verb = application.Scope == InstallScope.AllUsers ? "runas" : ""
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo);
    }

    private static Process? StartStoreUninstall(InstalledApplication application)
    {
        var escapedPackage = application.PackageFullName.Replace("'", "''", StringComparison.Ordinal);
        var script = $"Remove-AppxPackage -Package '{escapedPackage}' -Confirm:$false -ErrorAction Stop";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return Process.Start(startInfo);
    }

    private static void ConvertMsiMaintenanceToUninstall(List<string> arguments)
    {
        if (arguments.Count == 0) return;
        if (arguments[0].Equals("/I", StringComparison.OrdinalIgnoreCase))
        {
            arguments[0] = "/X";
        }
        else if (arguments[0].StartsWith("/I{", StringComparison.OrdinalIgnoreCase))
        {
            arguments[0] = "/X" + arguments[0][2..];
        }
    }
}
