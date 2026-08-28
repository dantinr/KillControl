namespace Kill.Services;

public static class ServiceSafetyPolicy
{
    private static readonly string[] CriticalServiceNames =
    [
        "BFE", "BrokerInfrastructure", "CryptSvc", "DcomLaunch", "Dhcp", "Dnscache", "EventLog",
        "LanmanServer", "LanmanWorkstation", "LSM", "mpssvc", "PlugPlay", "Power", "RpcEptMapper",
        "RpcSs", "SamSs", "Schedule", "SecurityHealthService", "SENS", "SystemEventsBroker",
        "TrustedInstaller", "UserManager", "WinDefend", "Winmgmt", "WlanSvc", "wuauserv"
    ];

    public static bool IsProtected(string serviceName, string executablePath, string serviceDllPath,
        string serviceType, string companyName)
    {
        if (CriticalServiceNames.Contains(serviceName, StringComparer.OrdinalIgnoreCase)) return true;
        if (serviceType.Contains("Driver", StringComparison.OrdinalIgnoreCase)) return true;
        if (companyName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return true;

        var target = !string.IsNullOrWhiteSpace(serviceDllPath) ? serviceDllPath : executablePath;
        if (string.IsNullOrWhiteSpace(target)) return true;
        if (IsUnderWindows(target)) return true;

        var executableName = Path.GetFileName(executablePath);
        if (executableName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(serviceDllPath)) return true;
        return false;
    }

    public static string ExpandServicePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded[12..]);
        }
        return expanded;
    }

    private static bool IsUnderWindows(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(ExpandServicePath(path));
            var windows = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            return fullPath.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
