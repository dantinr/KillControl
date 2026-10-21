using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ProcessSentinel.Core;

// Create in the UI's ordinary security context, attach ETW, then resume the main thread.
public sealed class SuspendedProgram : IDisposable
{
    private readonly IntPtr processHandle, threadHandle;
    private bool resumed;
    public int Id { get; }
    private SuspendedProgram(ProcessInformation info) { processHandle = info.Process; threadHandle = info.Thread; Id = (int)info.ProcessId; }
    public static SuspendedProgram Create(string path, string arguments)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择存在的 .exe 文件。");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder($"\"{path}\"{(string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments)}");
        if (!CreateProcess(path, command, IntPtr.Zero, IntPtr.Zero, false, 4, IntPtr.Zero, Path.GetDirectoryName(path), ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "程序无法以普通权限启动；需要管理员权限的程序请先手动启动，再选择进程监控。");
        return new(info);
    }
    public void Resume()
    {
        if (ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        resumed = true;
    }
    public void Dispose()
    {
        if (!resumed) TerminateProcess(processHandle, 1);
        CloseHandle(threadHandle);
        CloseHandle(processHandle);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
