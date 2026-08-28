using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public sealed class ApplicationDiscoveryService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public async Task<IReadOnlyList<InstalledApplication>> GetApplicationsAsync(CancellationToken cancellationToken = default)
    {
        var desktopTask = Task.Run(ReadDesktopApplications, cancellationToken);
        var storeTask = ReadStoreApplicationsAsync(cancellationToken);
        await Task.WhenAll(desktopTask, storeTask);

        var results = desktopTask.Result.Concat(storeTask.Result)
            .GroupBy(app => $"{app.Kind}|{app.DisplayName}|{app.Version}|{app.Publisher}|{app.Scope}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return results;
    }

    private static List<InstalledApplication> ReadDesktopApplications()
    {
        var applications = new List<InstalledApplication>();
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, InstallScope.AllUsers),
            (RegistryHive.LocalMachine, RegistryView.Registry32, InstallScope.AllUsers),
            (RegistryHive.CurrentUser, RegistryView.Registry64, InstallScope.CurrentUser),
            (RegistryHive.CurrentUser, RegistryView.Registry32, InstallScope.CurrentUser)
        };

        foreach (var (hive, view, scope) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstallKey.OpenSubKey(subKeyName);
                        if (key is null) continue;

                        var displayName = ReadString(key, "DisplayName").Trim();
                        if (string.IsNullOrWhiteSpace(displayName) || ReadInt(key, "SystemComponent") == 1) continue;
                        if (!string.IsNullOrWhiteSpace(ReadString(key, "ParentKeyName"))) continue;

                        var releaseType = ReadString(key, "ReleaseType");
                        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;

                        var uninstall = ReadString(key, "UninstallString");
                        var quietUninstall = ReadString(key, "QuietUninstallString");
                        var installLocation = ReadString(key, "InstallLocation");
                        var noRemove = ReadInt(key, "NoRemove") == 1;
                        var sizeKb = ReadLong(key, "EstimatedSize");

                        applications.Add(new InstalledApplication
                        {
                            Id = $"reg:{hive}:{view}:{subKeyName}",
                            DisplayName = displayName,
                            Version = ReadString(key, "DisplayVersion"),
                            Publisher = ReadString(key, "Publisher"),
                            InstallDate = FormatInstallDate(ReadString(key, "InstallDate")),
                            EstimatedSizeBytes = sizeKb > 0 ? sizeKb * 1024 : null,
                            InstallLocation = installLocation,
                            UninstallCommand = uninstall,
                            QuietUninstallCommand = quietUninstall,
                            RegistryKeyPath = $@"{UninstallPath}\{subKeyName}",
                            RegistryHive = hive,
                            RegistryView = view,
                            Kind = ApplicationKind.Desktop,
                            Scope = scope,
                            CanUninstall = !noRemove && (!string.IsNullOrWhiteSpace(uninstall) || !string.IsNullOrWhiteSpace(quietUninstall)),
                            IsProtected = SafetyPolicy.IsProtectedInstallLocation(installLocation)
                        });
                    }
                    catch (Exception)
                    {
                        // A malformed or inaccessible uninstall entry should not hide other applications.
                    }
                }
            }
            catch (Exception)
            {
                // Registry views may be unavailable on some Windows editions.
            }
        }

        return applications;
    }

    private static async Task<List<InstalledApplication>> ReadStoreApplicationsAsync(CancellationToken cancellationToken)
    {
        const string script = "$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " +
                              "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable } | ForEach-Object { " +
                              "$n=$_.Name; try { $d=(Get-AppxPackageManifest -Package $_.PackageFullName -ErrorAction Stop).Package.Properties.DisplayName; " +
                              "if ($d -and $d -notlike 'ms-resource:*') { $n=$d } } catch {}; " +
                              "[PSCustomObject]@{Name=$n;PackageFullName=$_.PackageFullName;Publisher=$_.Publisher;Version=$_.Version.ToString();InstallLocation=$_.InstallLocation} " +
                              "} | ConvertTo-Json -Compress";

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

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return [];
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var json = await outputTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return [];
            return ParseStoreApplications(json);
        }
        catch
        {
            return [];
        }
    }

    private static List<InstalledApplication> ParseStoreApplications(string json)
    {
        using var document = JsonDocument.Parse(json);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];

        var applications = new List<InstalledApplication>();
        foreach (var element in elements)
        {
            var name = GetJsonString(element, "Name");
            var package = GetJsonString(element, "PackageFullName");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(package)) continue;

            applications.Add(new InstalledApplication
            {
                Id = $"appx:{package}",
                DisplayName = name,
                Version = GetJsonString(element, "Version"),
                Publisher = FriendlyPublisher(GetJsonString(element, "Publisher")),
                InstallLocation = GetJsonString(element, "InstallLocation"),
                PackageFullName = package,
                Kind = ApplicationKind.Store,
                Scope = InstallScope.CurrentUser,
                CanUninstall = true,
                IsProtected = false
            });
        }
        return applications;
    }

    private static string GetJsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ToString() : "";

    private static string FriendlyPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "未知发布者";
        var cn = publisher.Split(',')
            .FirstOrDefault(part => part.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
        return cn is null ? publisher : cn.Trim()[3..];
    }

    private static string ReadString(RegistryKey key, string name) => key.GetValue(name)?.ToString() ?? "";

    private static int ReadInt(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int value => value,
        string text when int.TryParse(text, out var value) => value,
        _ => 0
    };

    private static long ReadLong(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int value => value,
        long value => value,
        string text when long.TryParse(text, out var value) => value,
        _ => 0
    };

    private static string FormatInstallDate(string value) =>
        DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd")
            : value;
}
