using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public sealed class ServiceDiscoveryService
{
    public async Task<IReadOnlyList<ManagedService>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        const string script = "$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
                              "Get-CimInstance Win32_Service | Select-Object Name,DisplayName,Description,State,StartMode," +
                              "ProcessId,PathName,StartName,ServiceType,AcceptStop | ConvertTo-Json -Compress";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Windows 服务查询。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"无法读取 Windows 服务：{error}".Trim());

        var blacklisted = ServiceBlacklistStore.Read().Select(entry => entry.ServiceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Parse(output, blacklisted)
            .OrderBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<ManagedService> Parse(string json, HashSet<string> blacklisted)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        using var document = JsonDocument.Parse(json);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];

        foreach (var element in elements)
        {
            var name = GetString(element, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var imageCommand = GetString(element, "PathName");
            var executablePath = ResolveExecutable(imageCommand);
            var serviceDll = ReadServiceDll(name);
            var processId = GetInt(element, "ProcessId");
            var processPath = ReadProcessPath(processId);
            var company = ReadCompanyName(!string.IsNullOrWhiteSpace(serviceDll) ? serviceDll : executablePath);
            var type = GetString(element, "ServiceType");

            yield return new ManagedService
            {
                Name = name,
                DisplayName = GetString(element, "DisplayName") is { Length: > 0 } displayName ? displayName : name,
                Description = GetString(element, "Description"),
                State = GetString(element, "State"),
                StartMode = GetString(element, "StartMode"),
                ProcessId = processId,
                ImageCommand = imageCommand,
                ExecutablePath = executablePath,
                ServiceDllPath = serviceDll,
                ProcessPath = processPath,
                StartAccount = GetString(element, "StartName"),
                ServiceType = type,
                CompanyName = company,
                IsProtected = ServiceSafetyPolicy.IsProtected(name, executablePath, serviceDll, type, company),
                IsBlacklisted = blacklisted.Contains(name),
                AcceptsStop = GetBool(element, "AcceptStop")
            };
        }
    }

    private static string ResolveExecutable(string command)
    {
        try { return ServiceSafetyPolicy.ExpandServicePath(WindowsCommandLine.ResolveExecutable(command).Executable); }
        catch { return ""; }
    }

    private static string ReadServiceDll(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}\Parameters");
            return ServiceSafetyPolicy.ExpandServicePath(key?.GetValue("ServiceDll")?.ToString() ?? "");
        }
        catch { return ""; }
    }

    private static string ReadProcessPath(int processId)
    {
        if (processId <= 0) return "";
        try { return Process.GetProcessById(processId).MainModule?.FileName ?? ""; }
        catch { return ""; }
    }

    private static string ReadCompanyName(string path)
    {
        try
        {
            if (!File.Exists(path)) return "未知发布者";
            return FileVersionInfo.GetVersionInfo(path).CompanyName is { Length: > 0 } company ? company : "未知发布者";
        }
        catch { return "未知发布者"; }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
