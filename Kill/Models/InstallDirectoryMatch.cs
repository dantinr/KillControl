namespace Kill.Models;

public sealed record InstallDirectoryMatch(
    InstalledApplication Application,
    string DirectoryPath,
    IReadOnlyList<string> Evidence,
    bool CanUseSelectedDirectoryForCleanup,
    int MatchScore)
{
    public string DisplayName => Application.DisplayName;
    public string PublisherVersionLabel => $"{Application.PublisherLabel}  ·  {Application.VersionLabel}";
    public string EvidenceLabel => string.Join("；", Evidence);
    public string CleanupScopeLabel => CanUseSelectedDirectoryForCleanup
        ? "卸载后可将所选目录列为高风险残留，默认不选中"
        : "所选目录仅用于识别，不会作为整体清理";
    public string UninstallAvailabilityLabel => Application.CanUninstall ? "可运行官方卸载" : "没有登记可用的卸载入口";
}
