using System.Runtime.InteropServices;

namespace Kill.Services;

internal static class WindowsCommandLine
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".cmd", ".bat"];

    public static IReadOnlyList<string> Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("无法解析卸载命令。", new System.ComponentModel.Win32Exception());

        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
            {
                var itemPointer = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(itemPointer) ?? "";
            }
            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public static (string Executable, IReadOnlyList<string> Arguments) ResolveExecutable(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) throw new InvalidOperationException("卸载命令为空。");

        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var parsed = Split(expanded);
        if (parsed.Count == 0 || string.IsNullOrWhiteSpace(parsed[0]))
            throw new InvalidOperationException("卸载命令为空。");

        var parsedExecutable = parsed[0];
        if (File.Exists(parsedExecutable) || IsPathSearchableExecutable(parsedExecutable))
            return (parsedExecutable, parsed.Skip(1).ToArray());

        if (!expanded.StartsWith('"'))
        {
            var unquoted = ResolveUnquotedExecutable(expanded);
            if (unquoted is not null) return unquoted.Value;
        }

        return (parsedExecutable, parsed.Skip(1).ToArray());
    }

    private static (string Executable, IReadOnlyList<string> Arguments)? ResolveUnquotedExecutable(string commandLine)
    {
        for (var index = 0; index < commandLine.Length; index++)
        {
            foreach (var extension in ExecutableExtensions)
            {
                if (index + extension.Length > commandLine.Length ||
                    !commandLine.AsSpan(index, extension.Length).Equals(extension, StringComparison.OrdinalIgnoreCase)) continue;

                var executableEnd = index + extension.Length;
                var candidate = commandLine[..executableEnd].Trim();
                if (!File.Exists(candidate)) continue;

                var argumentText = commandLine[executableEnd..].TrimStart();
                return (candidate, SplitArgumentTail(argumentText));
            }
        }
        return null;
    }

    private static IReadOnlyList<string> SplitArgumentTail(string argumentText)
    {
        if (string.IsNullOrWhiteSpace(argumentText)) return [];
        return Split($"placeholder.exe {argumentText}").Skip(1).ToArray();
    }

    private static bool IsPathSearchableExecutable(string executable) =>
        !Path.IsPathRooted(executable) &&
        !executable.Contains(Path.DirectorySeparatorChar) &&
        !executable.Contains(Path.AltDirectorySeparatorChar);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
