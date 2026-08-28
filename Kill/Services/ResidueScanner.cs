using Kill.Models;
using Microsoft.Win32;

namespace Kill.Services;

public sealed class ResidueScanner
{
    public Task<IReadOnlyList<CleanupCandidate>> ScanAsync(InstalledApplication application, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<CleanupCandidate>>(() => Scan(application, cancellationToken), cancellationToken);

    private static IReadOnlyList<CleanupCandidate> Scan(InstalledApplication application, CancellationToken cancellationToken)
    {
        var results = new List<CleanupCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddInstallLocation(application, results, seen);

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanDirectoryRoot(root, application, results, seen);
        }

        AddOriginalRegistryEntry(application, results, seen);
        ScanRegistry(RegistryHive.CurrentUser, RegistryView.Default, application, results, seen);
        ScanRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, application, results, seen);
        ScanRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, application, results, seen);

        return results.OrderBy(item => item.Risk).ThenBy(item => item.DisplayTarget).ToList();
    }

    private static void AddInstallLocation(InstalledApplication app, List<CleanupCandidate> results, HashSet<string> seen)
    {
        if (app.Kind == ApplicationKind.Store) return;
        if (string.IsNullOrWhiteSpace(app.InstallLocation) || !Directory.Exists(app.InstallLocation)) return;
        if (!SafetyPolicy.IsPathSafeToQuarantine(app.InstallLocation)) return;

        AddFileCandidate(results, seen, app.InstallLocation, CleanupItemKind.Directory,
            "安装记录指向此目录；它可能包含共享组件，请核对内容。", RiskLevel.High, false);
    }

    private static void ScanDirectoryRoot(string root, InstalledApplication app, List<CleanupCandidate> results, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (SafetyPolicy.IsStrongNameMatch(name, app.DisplayName) && SafetyPolicy.IsPathSafeToQuarantine(directory))
                {
                    AddFileCandidate(results, seen, directory, CleanupItemKind.Directory,
                        "文件夹名称与应用名称精确匹配。", RiskLevel.Low, true);
                }

                if (!PublisherMatches(name, app.Publisher)) continue;
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (SafetyPolicy.IsStrongNameMatch(Path.GetFileName(child), app.DisplayName) &&
                            SafetyPolicy.IsPathSafeToQuarantine(child))
                        {
                            AddFileCandidate(results, seen, child, CleanupItemKind.Directory,
                                "发布者目录中的子文件夹与应用名称精确匹配。", RiskLevel.Medium, false);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static void AddOriginalRegistryEntry(InstalledApplication app, List<CleanupCandidate> results, HashSet<string> seen)
    {
        if (app.RegistryHive is null || app.RegistryView is null || string.IsNullOrWhiteSpace(app.RegistryKeyPath)) return;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(app.RegistryHive.Value, app.RegistryView.Value);
            using var key = baseKey.OpenSubKey(app.RegistryKeyPath);
            if (key is null) return;

            var hiveName = app.RegistryHive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";
            AddRegistryCandidate(results, seen, $@"{hiveName}\{app.RegistryKeyPath}", app.RegistryView.Value,
                "官方卸载后仍保留的原始卸载登记项。", RiskLevel.Medium, true);
        }
        catch { }
    }

    private static void ScanRegistry(RegistryHive hive, RegistryView view, InstalledApplication app,
        List<CleanupCandidate> results, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var software = baseKey.OpenSubKey("SOFTWARE");
            if (software is null) return;
            var hiveName = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

            foreach (var subName in software.GetSubKeyNames())
            {
                if (SafetyPolicy.IsStrongNameMatch(subName, app.DisplayName))
                {
                    AddRegistryCandidate(results, seen, $@"{hiveName}\SOFTWARE\{subName}", view,
                        "注册表项名称与应用名称精确匹配。", RiskLevel.Medium, false);
                }

                if (!PublisherMatches(subName, app.Publisher)) continue;
                using var publisherKey = software.OpenSubKey(subName);
                if (publisherKey is null) continue;
                foreach (var childName in publisherKey.GetSubKeyNames())
                {
                    if (SafetyPolicy.IsStrongNameMatch(childName, app.DisplayName))
                    {
                        AddRegistryCandidate(results, seen, $@"{hiveName}\SOFTWARE\{subName}\{childName}", view,
                            "发布者项下的注册表键与应用名称精确匹配。", RiskLevel.High, false);
                    }
                }
            }
        }
        catch { }
    }

    private static bool PublisherMatches(string keyName, string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher) || publisher == "未知发布者") return false;
        return SafetyPolicy.NormalizeName(keyName).Equals(SafetyPolicy.NormalizeName(publisher), StringComparison.Ordinal);
    }

    private static void AddFileCandidate(List<CleanupCandidate> results, HashSet<string> seen, string target,
        CleanupItemKind kind, string reason, RiskLevel risk, bool selected)
    {
        var fullPath = SafetyPolicy.NormalizePath(target);
        if (!seen.Add($"file:{fullPath}")) return;
        results.Add(new CleanupCandidate
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Target = fullPath,
            DisplayTarget = fullPath,
            Reason = reason,
            Risk = risk,
            IsSelected = selected
        });
    }

    private static void AddRegistryCandidate(List<CleanupCandidate> results, HashSet<string> seen, string target,
        RegistryView view, string reason, RiskLevel risk, bool selected)
    {
        var key = $"reg:{view}:{target}";
        if (!seen.Add(key)) return;
        results.Add(new CleanupCandidate
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = CleanupItemKind.RegistryKey,
            Target = target,
            DisplayTarget = $"{target} ({(view == RegistryView.Registry32 ? "32 位" : "64 位")})",
            RegistryViewName = view.ToString(),
            Reason = reason,
            Risk = risk,
            IsSelected = selected
        });
    }
}
