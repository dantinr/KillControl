using Microsoft.Win32;

namespace Kill.Models;

public enum ApplicationKind
{
    Desktop,
    Store
}

public enum InstallScope
{
    CurrentUser,
    AllUsers
}

public sealed class InstalledApplication
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "未知发布者";
    public string InstallDate { get; init; } = "";
    public long? EstimatedSizeBytes { get; init; }
    public string InstallLocation { get; init; } = "";
    public string UninstallCommand { get; init; } = "";
    public string QuietUninstallCommand { get; init; } = "";
    public string PackageFullName { get; init; } = "";
    public string RegistryKeyPath { get; init; } = "";
    public RegistryHive? RegistryHive { get; init; }
    public RegistryView? RegistryView { get; init; }
    public ApplicationKind Kind { get; init; }
    public InstallScope Scope { get; init; }
    public bool CanUninstall { get; init; }
    public bool IsProtected { get; init; }

    public string KindLabel => Kind == ApplicationKind.Store ? "商店应用" : "桌面应用";
    public string ScopeLabel => Scope == InstallScope.AllUsers ? "所有用户" : "当前用户";
    public string VersionLabel => string.IsNullOrWhiteSpace(Version) ? "版本未知" : $"版本 {Version}";
    public string PublisherLabel => string.IsNullOrWhiteSpace(Publisher) ? "未知发布者" : Publisher;
    public string SizeLabel => EstimatedSizeBytes switch
    {
        >= 1024L * 1024 * 1024 => $"{EstimatedSizeBytes.Value / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{EstimatedSizeBytes.Value / (1024d * 1024):0.#} MB",
        >= 1024L => $"{EstimatedSizeBytes.Value / 1024d:0.#} KB",
        _ => "未知"
    };

    public string Initials
    {
        get
        {
            var text = DisplayName.Trim();
            if (text.Length == 0) return "?";
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 1
                ? string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])))
                : text[..Math.Min(2, text.Length)].ToUpperInvariant();
        }
    }
}
