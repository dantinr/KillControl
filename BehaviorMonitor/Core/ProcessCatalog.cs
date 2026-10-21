using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessSentinel.Core;

public static class ProcessCatalog
{
    public static IReadOnlyList<ProcessInfo> Snapshot()
    {
        var entries = new List<ProcessInfo>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var item = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref item)) return entries;
            do
            {
                var id = (int)item.ProcessId;
                entries.Add(ReadDetails(new(id, (int)item.ParentProcessId, item.ExeFile ?? "", "", 0)));
            } while (Process32Next(snapshot, ref item));
        }
        finally { CloseHandle(snapshot); }
        return entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToArray();
    }

    public static ProcessInfo Get(int id) => Snapshot().FirstOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("目标进程已退出，请刷新进程列表。");

    public static ProcessInfo ReadDetails(ProcessInfo value)
    {
        try
        {
            using var process = Process.GetProcessById(value.Id);
            value = value with { StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks };
            var handle = OpenProcess(0x1000, false, value.Id);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buffer = new char[32768];
                    var length = buffer.Length;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref length))
                        value = value with { Path = new string(buffer, 0, length) };
                }
                finally { CloseHandle(handle); }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
        return value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
