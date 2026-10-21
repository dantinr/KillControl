using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ProcessSentinel.Core;
using ProcessSentinel.Core.Networking;

internal static class NetworkMonitorChecks
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        foreach (bool ipv6 in new[] { false, true })
            foreach (bool tcp in new[] { false, true })
                yield return ($"网络连接表布局 {(ipv6 ? "IPv6" : "IPv4")} {(tcp ? "TCP" : "UDP")}", () =>
                {
                    int stride = ipv6 ? tcp ? 56 : 28 : tcp ? 24 : 12;
                    var table = new byte[4 + stride];
                    BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
                    void Number(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + offset), value);
                    void Port(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4 + offset), value);
                    var local = IPAddress.Parse(ipv6 ? "fe80::1234" : "127.1.2.3");
                    local.GetAddressBytes().CopyTo(table, 4 + (tcp && !ipv6 ? 4 : 0));
                    Port(ipv6 ? 20 : tcp ? 8 : 4, 54321);
                    Number(ipv6 ? tcp ? 52 : 24 : tcp ? 20 : 8, 12345);
                    if (ipv6) Number(16, 7);
                    if (tcp)
                    {
                        Number(ipv6 ? 48 : 0, 5);
                        IPAddress.Parse(ipv6 ? "2001:db8::abcd" : "192.0.2.7").GetAddressBytes().CopyTo(table, 4 + (ipv6 ? 24 : 12));
                        Port(ipv6 ? 44 : 16, 443);
                    }
                    var row = NetworkConnectionReader.Decode(table, ipv6, tcp).Single();
                    Require(row.ProcessId == 12345 && row.LocalPort == 54321 && row.LocalAddress == (ipv6 ? "fe80::1234%7" : "127.1.2.3") &&
                        row.RemotePort == (tcp ? 443 : 0) && row.State == (tcp ? "ESTABLISHED" : "UDP_BOUND"), "布局或字节序错误");
                    return Task.CompletedTask;
                }
                );
        yield return ("网络连接表拒绝截断与非法行数", () =>
        {
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 255, 255, 255, 255 }, new byte[] { 1, 0, 0, 0 } })
            {
                try { NetworkConnectionReader.Decode(bytes, true, true); }
                catch (InvalidDataException) { continue; }
                throw new InvalidOperationException("接受了损坏的连接表");
            }
            return Task.CompletedTask;
        }
        );
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            yield return ($"本机连接表识别自建 {address.AddressFamily} TCP/UDP 端点", async () =>
            {
                using var listener = new TcpListener(address, 0);
                listener.Start();
                using var udp = new UdpClient(new IPEndPoint(address, 0));
                using var client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                using var accepted = await listener.AcceptTcpClientAsync();
                var rows = NetworkConnectionReader.Capture().Where(row => row.ProcessId == Environment.ProcessId).ToArray();
                int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                Require(rows.Any(row => row.Protocol == NetworkTransport.TCP && row.LocalPort == serverPort && row.State == "LISTEN" && row.RemoteAddress == ""), "监听端点错误");
                Require(rows.Any(row => row.Protocol == NetworkTransport.TCP && row.LocalPort == clientPort && row.RemotePort == serverPort && row.State == "ESTABLISHED" && row.LocalAddress == address.ToString()), "TCP 本地与远端端口错误");
                Require(rows.Any(row => row.Protocol == NetworkTransport.UDP && row.LocalPort == ((IPEndPoint)udp.Client.LocalEndPoint!).Port && row.RemoteAddress == ""), "UDP 绑定端点错误");
            }
            );
        yield return ("本机 IPv6 链路本地端点保留真实网卡作用域", () =>
        {
            var address = NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Select(item => item.Address).FirstOrDefault(ip => ip.IsIPv6LinkLocal);
            if (address is null) return Task.CompletedTask;
            using var listener = new TcpListener(address, 0);
            listener.Start();
            using var udp = new UdpClient(new IPEndPoint(address, 0));
            int tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var rows = NetworkConnectionReader.Capture().Where(row => row.ProcessId == Environment.ProcessId &&
                (row.Protocol == NetworkTransport.TCP && row.LocalPort == tcpPort || row.Protocol == NetworkTransport.UDP && row.LocalPort == udpPort)).ToArray();
            Require(rows.Length == 2 && rows.All(row => row.LocalAddress == address.ToString()), "IPv6 作用域编号不符合实际绑定接口");
            return Task.CompletedTask;
        }
        );
        yield return ("网络流量分方向、协议汇总并计算采样速率", () =>
        {
            var t = DateTimeOffset.UtcNow;
            var tracker = new NetworkFlowTracker(t);
            var process = Process(t);
            var tcp = Endpoint();
            tracker.Record(tcp, process, t.AddSeconds(1), 2000);
            tracker.Record(tcp, process, t.AddSeconds(1), 1000, receive: true);
            tracker.Record(tcp with { Protocol = NetworkTransport.UDP }, process, t.AddSeconds(1), 500);
            var first = tracker.Snapshot(t.AddSeconds(2));
            var second = tracker.Snapshot(t.AddSeconds(4));
            Require(first.Flows.Count == 2 && first.SentBytes == 2500 && first.ReceivedBytes == 1000 && first.SendBytesPerSecond == 1250 &&
                first.Flows.Single(row => row.Endpoint.Protocol == NetworkTransport.TCP).ReceiveBytesPerSecond == 500 && second.SendBytesPerSecond == 0 &&
                first.Flows.Single(row => row.Endpoint.Protocol == NetworkTransport.TCP).SentBytes == 2000, "方向、协议、速率或旧快照发生错误");
            return Task.CompletedTask;
        }
        );
        yield return ("网络流量隔离 PID 重用、重连与过期事件", () =>
        {
            var t = DateTimeOffset.UtcNow;
            var tracker = new NetworkFlowTracker(t);
            var process = Process(t);
            tracker.Record(Endpoint(), process, t.AddSeconds(-1), 999);
            tracker.Record(Endpoint(), process with { StartTimeUtcTicks = t.AddSeconds(10).UtcTicks }, t.AddSeconds(1), 999);
            tracker.Record(Endpoint(), process, t.AddSeconds(1), 100, connectionId: 1);
            tracker.Record(Endpoint(), process, t.AddSeconds(2), 200, connectionId: 2);
            tracker.Record(Endpoint(), process with { StartTimeUtcTicks = t.AddSeconds(3).UtcTicks }, t.AddSeconds(4), 300);
            var snapshot = tracker.Snapshot(t.AddSeconds(5));
            Require(snapshot.SentBytes == 600 && snapshot.Flows.Count == 3 && snapshot.Flows.Count(row => row.EndedAtUtc is not null) == 1 &&
                snapshot.Flows.Select(row => row.Id).Distinct().Count() == 3, "跨进程实例或重连串入旧流量");
            tracker.ProcessExited(process.Id, t.AddSeconds(6));
            Require(tracker.Snapshot(t.AddSeconds(7)).Flows.All(row => row.EndedAtUtc is not null), "退出进程仍显示活动");
            return Task.CompletedTask;
        }
        );
        yield return ("UDP 远端活动保留到绑定消失且停止后时长冻结", () =>
        {
            var t = DateTimeOffset.UtcNow;
            var process = Process(t);
            var tracker = new NetworkFlowTracker(t);
            var flow = Endpoint() with { Protocol = NetworkTransport.UDP };
            tracker.Record(flow, process, t.AddSeconds(1), 10);
            var bound = flow with { LocalAddress = "0.0.0.0", RemoteAddress = "", RemotePort = 0, State = "UDP_BOUND", Process = process };
            tracker.Observe([bound], t.AddSeconds(5));
            Require(tracker.Snapshot(t.AddSeconds(6)).Flows.All(row => row.EndedAtUtc is null), "UDP 活动被错误结束");
            tracker.Observe([bound], t.AddSeconds(40));
            Require(tracker.Snapshot(t.AddSeconds(40)).Flows.Any(row => row.Endpoint.State == "UDP_IDLE"), "UDP 空闲状态错误");
            tracker.Observe([], t.AddSeconds(41));
            var stopped = tracker.Snapshot(t.AddSeconds(42), false);
            Require(stopped.Flows.All(row => row.EndedAtUtc is not null) && !stopped.Running && stopped.SendBytesPerSecond == 0 &&
                tracker.Snapshot(t.AddSeconds(50), false).Flows.First().EndedAtUtc == stopped.Flows.First().EndedAtUtc, "停止后观测时间仍增长");
            return Task.CompletedTask;
        }
        );
        yield return ("网络容量限制保留总量并防止计数溢出", () =>
        {
            var t = DateTimeOffset.UtcNow;
            var tracker = new NetworkFlowTracker(t, 1);
            tracker.Record(Endpoint(), Process(t), t, long.MaxValue);
            tracker.Record(Endpoint() with { RemotePort = 80 }, Process(t), t, 10);
            tracker.Record(Endpoint(), Process(t), t, -1, receive: true);
            var snapshot = tracker.Snapshot(t.AddSeconds(1));
            Require(snapshot.Flows.Count == 1 && snapshot.SentBytes == long.MaxValue && snapshot.ReceivedBytes == 0 && snapshot.OmittedFlows > 0,
                "容量限制或饱和计数错误");
            return Task.CompletedTask;
        }
        );
    }

    private static ProcessInfo Process(DateTimeOffset t) => new(12345, 0, "network-test", "", t.AddSeconds(-10).UtcTicks);
    private static NetworkEndpoint Endpoint() => new(NetworkTransport.TCP, 12345, "127.0.0.1", 54321, "127.0.0.1", 443, "ESTABLISHED");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
