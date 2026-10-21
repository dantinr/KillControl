using System.IO;

namespace ProcessSentinel.App;

internal static class SessionLogs
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessSentinel", "Sessions");

    internal static string ResolveDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultDirectory;
        string path = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("日志目录需要使用完整路径，例如 D:\\ProcessSentinelLogs。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static void VerifyWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        string probe = Path.Combine(directory, ".ProcessSentinel-write-check-" + Guid.NewGuid().ToString("N"));
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
        stream.Flush();
    }
}
