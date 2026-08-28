using System.Text;
using Kill.Models;

namespace Kill.Services;

public static class SafetyPolicy
{
    private static readonly string[] GenericNames =
    [
        "app", "application", "apps", "bin", "common", "data", "files", "program", "programs",
        "software", "system", "tools", "windows"
    ];

    public static bool IsProtectedInstallLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string fullPath;
        try { fullPath = NormalizePath(path); }
        catch { return true; }

        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        var pathRoot = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(pathRoot) && PathsEqual(fullPath, pathRoot)) return true;
        if (protectedRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root => PathsEqual(fullPath, root))) return true;
        var windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrWhiteSpace(windowsRoot) && IsUnder(fullPath, NormalizePath(windowsRoot));
    }

    public static bool IsPathSafeToQuarantine(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsProtectedInstallLocation(path)) return false;

        try
        {
            var fullPath = NormalizePath(path);
            var blockedTrees = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")
            };
            if (blockedTrees.Where(root => !string.IsNullOrWhiteSpace(root))
                .Any(root => IsUnder(fullPath, NormalizePath(root)) || PathsEqual(fullPath, root))) return false;

            var leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
            return leaf.Length >= 2 && !GenericNames.Contains(NormalizeName(leaf));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsStrongNameMatch(string candidateName, string applicationName)
    {
        var candidate = NormalizeName(candidateName);
        var app = NormalizeName(applicationName);
        return candidate.Length >= 3 && app.Length >= 3 && candidate.Equals(app, StringComparison.Ordinal);
    }

    public static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize())
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    public static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))));

    public static bool PathsEqual(string first, string second) =>
        string.Equals(NormalizePath(first), NormalizePath(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
