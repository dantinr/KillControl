using System.Globalization;
using Kill.Models;

namespace Kill.Services;

public sealed class InstallDirectoryIdentificationService
{
    public IReadOnlyList<InstallDirectoryMatch> FindMatches(
        string directoryPath, IEnumerable<InstalledApplication> applications)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new InvalidOperationException("请选择具体的软件安装目录。");
        var selectedDirectory = SafetyPolicy.NormalizePath(directoryPath);
        if (!SafetyPolicy.IsPathSafeToQuarantine(selectedDirectory))
            throw new InvalidOperationException("所选目录属于系统、共享或范围过大的位置，不能用于识别卸载。");

        var matches = new List<InstallDirectoryMatch>();
        foreach (var application in applications.Where(app => app.Kind == ApplicationKind.Desktop))
        {
            var evidence = new List<string>();
            var score = 0;
            var canUseForCleanup = false;

            if (PathsEqualIfValid(application.InstallLocation, selectedDirectory))
            {
                evidence.Add("安装登记位置完全一致");
                score += 100;
                canUseForCleanup = true;
            }

            AddPathEvidence(application.DisplayIcon, "显示图标", selectedDirectory, evidence,
                ref score, ref canUseForCleanup, ResolveDisplayIconPath);
            AddPathEvidence(application.UninstallCommand, "官方卸载器", selectedDirectory, evidence,
                ref score, ref canUseForCleanup, ResolveCommandExecutable);
            AddPathEvidence(application.QuietUninstallCommand, "静默卸载器", selectedDirectory, evidence,
                ref score, ref canUseForCleanup, ResolveCommandExecutable);

            if (evidence.Count == 0) continue;
            matches.Add(new InstallDirectoryMatch(application, selectedDirectory, evidence,
                canUseForCleanup, score));
        }

        var ordered = matches
            .OrderByDescending(match => match.MatchScore)
            .ThenBy(match => match.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (ordered.Count <= 1) return ordered;

        return ordered.Select(match => match with { CanUseSelectedDirectoryForCleanup = false }).ToList();
    }

    internal static string? ResolveDisplayIconPath(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            candidate = closingQuote > 1 ? expanded[1..closingQuote] : expanded.Trim('"');
        }
        else
        {
            candidate = expanded;
            var comma = candidate.LastIndexOf(',');
            if (comma > 0 && int.TryParse(candidate[(comma + 1)..].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out _))
            {
                candidate = candidate[..comma];
            }
            candidate = candidate.Trim().Trim('"');
        }

        return NormalizeRootedPath(candidate);
    }

    private static void AddPathEvidence(string source, string sourceLabel, string selectedDirectory,
        List<string> evidence, ref int score, ref bool canUseForCleanup, Func<string, string?> resolver)
    {
        var resolvedPath = resolver(source);
        if (resolvedPath is null || !TryClassifyPath(resolvedPath, selectedDirectory, out var isDirectChild)) return;

        var label = isDirectChild
            ? $"{sourceLabel}直接位于所选目录"
            : $"{sourceLabel}位于所选目录的子目录";
        if (evidence.Contains(label, StringComparer.Ordinal)) return;
        evidence.Add(label);
        score += isDirectChild ? 60 : 30;
        canUseForCleanup |= isDirectChild;
    }

    private static string? ResolveCommandExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        try
        {
            return NormalizeRootedPath(WindowsCommandLine.ResolveExecutable(command).Executable);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeRootedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        try { return SafetyPolicy.NormalizePath(path); }
        catch { return null; }
    }

    private static bool PathsEqualIfValid(string path, string selectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return SafetyPolicy.PathsEqual(path, selectedDirectory); }
        catch { return false; }
    }

    private static bool TryClassifyPath(string filePath, string selectedDirectory, out bool isDirectChild)
    {
        isDirectChild = false;
        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(parent)) return false;
        if (SafetyPolicy.PathsEqual(parent, selectedDirectory))
        {
            isDirectChild = true;
            return true;
        }

        return filePath.StartsWith(selectedDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
