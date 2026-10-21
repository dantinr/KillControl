using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace ProcessSentinel.Core.Networking;

public static class NetworkConnectionReader
{
    public static IReadOnlyList<NetworkEndpoint> Capture()
    {
        var rows = new List<NetworkEndpoint>();
        foreach (int family in new[] { 2, 23 })
        {
            ReadTable(family, true, rows);
            ReadTable(family, false, rows);
        }
        var processes = rows.Select(row => row.ProcessId).Distinct().ToDictionary(id => id, ReadProcess);
        return rows.Select(row => row with { Process = processes[row.ProcessId] }).ToArray();
    }

    public static ProcessInfo ReadProcess(int id)
    {
        string name = id == 0 ? "系统/已退出" : $"PID {id}";
        try { using var process = Process.GetProcessById(id); name = process.ProcessName; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { }
        return id <= 0 ? new(id, 0, name, "", 0) : ProcessCatalog.ReadDetails(new(id, 0, name, "", 0));
    }

    private static void ReadTable(int family, bool tcp, List<NetworkEndpoint> rows)
    {
        int size = 0;
        uint Query(IntPtr buffer, ref int length) => tcp
            ? GetExtendedTcpTable(buffer, ref length, false, family, 5, 0)
            : GetExtendedUdpTable(buffer, ref length, false, family, 1, 0);
        uint result = Query(IntPtr.Zero, ref size);
        if (result != 0 && result != 122) throw new Win32Exception((int)result);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidDataException("网络连接表大小异常。");
            int allocated = size;
            var buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                result = Query(buffer, ref size);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                rows.AddRange(Decode(bytes, family == 23, tcp));
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("网络连接表变化过快，请稍后重试。");
    }

    // All four OWNER_PID layouts contain only DWORD-aligned members; the first row follows the DWORD count.
    public static IReadOnlyList<NetworkEndpoint> Decode(byte[] table, bool ipv6, bool tcp)
    {
        int stride = (ipv6, tcp) switch { (true, true) => 56, (true, false) => 28, (false, true) => 24, _ => 12 };
        if (table.Length < 4) throw new InvalidDataException("网络连接表不完整。");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > (table.Length - 4) / stride) throw new InvalidDataException("网络连接表行数越界。");
        var rows = new List<NetworkEndpoint>((int)count);
        for (int index = 0; index < count; index++)
        {
            int row = 4 + index * stride;
            uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(row + offset, 4));
            int Port(int offset) => BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(row + offset, 2));
            string Address(int offset, int scopeOffset = -1) => NetworkAddress.Normalize(ipv6
                // Windows returns scope IDs as host-order DWORDs; ports remain in network byte order.
                ? new IPAddress(table.AsSpan(row + offset, 16), Read(scopeOffset)).ToString()
                : new IPAddress(table.AsSpan(row + offset, 4)).ToString());
            string local = Address(tcp && !ipv6 ? 4 : 0, ipv6 ? 16 : -1);
            int localPort = Port(ipv6 ? 20 : tcp ? 8 : 4);
            uint state = tcp ? Read(ipv6 ? 48 : 0) : 0;
            string remote = tcp && state != 2 ? Address(ipv6 ? 24 : 12, ipv6 ? 40 : -1) : "";
            int remotePort = string.IsNullOrEmpty(remote) ? 0 : Port(ipv6 ? 44 : 16);
            int pid = checked((int)Read(ipv6 ? tcp ? 52 : 24 : tcp ? 20 : 8));
            rows.Add(new(tcp ? NetworkTransport.TCP : NetworkTransport.UDP, pid, local, localPort, remote, remotePort,
                tcp ? TcpState(state) : "UDP_BOUND"));
        }
        return rows;
    }

    private static string TcpState(uint value) => value switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RECEIVED",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT_1",
        7 => "FIN_WAIT_2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN"
    };

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
