using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using ProcessSentinel.Core.Networking;

namespace Kill;

internal static class NetworkDiagnostics
{
    internal static async Task RunAsync(App app, string reportPath)
    {
        var lines = new List<string>();
        var fixtures = new List<LoopbackFixture>();
        var window = new MainWindow("Network", true) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        int result = 1;
        try
        {
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                foreach (bool tcp in new[] { true, false }) fixtures.Add(await LoopbackFixture.CreateAsync(address, tcp));
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var network = (NetworkMonitorView)window.Navigate("Network");
            Check(!network.IsMonitoring, "network collection remains opt-in");
            await network.StartFromShellAsync();
            Check(network.IsMonitoring, "the internal collector starts: " + network.Diagnostic.Error);
            var observedRates = new HashSet<string>();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var fixture in fixtures) await fixture.ExchangeAsync();
                foreach (var fixture in fixtures)
                    if (network.LatestSnapshot?.Flows.Any(flow => fixture.Matches(flow) && flow.SendBytesPerSecond > 0 && flow.ReceiveBytesPerSecond > 0) == true)
                        observedRates.Add(fixture.Name);
                await Task.Delay(50);
            }
            await Task.Delay(3500);
            var snapshot = network.LatestSnapshot ?? throw new InvalidOperationException("No network snapshots arrived.");
            bool flowChecks = true;
            foreach (var fixture in fixtures)
            {
                var flows = snapshot.Flows.Where(fixture.Matches).ToArray();
                lines.Add($"{fixture.Name}: expected {fixture.Sent}/{fixture.Received} bytes; " +
                    string.Join("; ", snapshot.Flows.Where(flow => flow.Process.Id == Environment.ProcessId && fixture.Related(flow))
                        .Select(flow => $"{flow.Endpoint.LocalAddress}:{flow.Endpoint.LocalPort} -> {flow.Endpoint.RemoteAddress}:{flow.Endpoint.RemotePort}, " +
                            $"start={flow.Process.StartTimeUtcTicks}, bytes={flow.SentBytes}/{flow.ReceivedBytes}, state={flow.Endpoint.State}, ended={flow.EndedAtUtc is not null}")));
                bool bytesMatch = flows.Length == 1 && flows[0].SentBytes == fixture.Sent && flows[0].ReceivedBytes == fixture.Received;
                lines.Add((bytesMatch ? "PASS: " : "FAIL: ") + fixture.Name + " attributes both byte directions to the exact PID and local/remote ports");
                bool rateMatch = observedRates.Contains(fixture.Name);
                lines.Add((rateMatch ? "PASS: " : "FAIL: ") + fixture.Name + " reports nonzero live send/receive rates");
                flowChecks &= bytesMatch && rateMatch;
            }
            Check(flowChecks, "all four loopback fixture flows pass");
            Check(snapshot.EventsLost == 0 && snapshot.OmittedFlows == 0 && string.IsNullOrEmpty(snapshot.Warning),
                "the probe has no lost events, truncated rows or connection-table failures");
            window.Navigate("Applications");
            await Task.Delay(1200);
            Check(network.IsMonitoring && network.LatestSnapshot!.CapturedAtUtc > snapshot.CapturedAtUtc,
                "network collection continues while another page is selected");
            await window.CloseForDiagnosticsAsync();
            Check(!network.IsMonitoring && network.LatestSnapshot is { Running: false } stopped && stopped.Flows.All(flow => flow.EndedAtUtc is not null),
                "closing the main window drains the collector and freezes all observations");
            foreach (var fixture in fixtures) await fixture.ExchangeAsync();
            Check(true, "stopping monitoring preserves all fixture sockets and their transfers");
            result = 0;
        }
        catch (Exception exception) { lines.Add("FAIL: " + exception); }
        finally
        {
            try { if (window.IsVisible) await window.CloseForDiagnosticsAsync(); }
            catch (Exception exception) { lines.Add("FAIL: cleanup: " + exception); result = 1; }
            foreach (var fixture in fixtures) fixture.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllLinesAsync(reportPath, lines);
            app.Shutdown(result);
        }

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            lines.Add("PASS: " + description);
        }
    }

    private sealed class LoopbackFixture : IDisposable
    {
        private TcpListener? listener;
        private TcpClient? client, server;
        private UdpClient? udpClient, udpServer;
        private IPAddress address = IPAddress.Loopback;
        private int localPort, remotePort;
        private NetworkTransport protocol;
        internal long Sent { get; private set; }
        internal long Received { get; private set; }
        internal string Name => $"{address.AddressFamily} {protocol}";

        internal static async Task<LoopbackFixture> CreateAsync(IPAddress address, bool tcp)
        {
            var fixture = new LoopbackFixture { address = address, protocol = tcp ? NetworkTransport.TCP : NetworkTransport.UDP };
            try
            {
                if (tcp)
                {
                    fixture.listener = new TcpListener(address, 0);
                    fixture.listener.Start();
                    fixture.remotePort = ((IPEndPoint)fixture.listener.LocalEndpoint).Port;
                    fixture.client = new TcpClient(address.AddressFamily);
                    await fixture.client.ConnectAsync(address, fixture.remotePort);
                    fixture.server = await fixture.listener.AcceptTcpClientAsync();
                    fixture.localPort = ((IPEndPoint)fixture.client.Client.LocalEndPoint!).Port;
                }
                else
                {
                    fixture.udpClient = new UdpClient(new IPEndPoint(address, 0));
                    fixture.udpServer = new UdpClient(new IPEndPoint(address, 0));
                    fixture.localPort = ((IPEndPoint)fixture.udpClient.Client.LocalEndPoint!).Port;
                    fixture.remotePort = ((IPEndPoint)fixture.udpServer.Client.LocalEndPoint!).Port;
                }
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        internal bool Matches(NetworkFlow flow) => flow.Process.Id == Environment.ProcessId && flow.Endpoint.Protocol == protocol &&
            flow.Endpoint.LocalAddress == address.ToString() && flow.Endpoint.LocalPort == localPort && flow.Endpoint.RemotePort == remotePort &&
            flow.Endpoint.RemoteAddress == address.ToString();

        internal bool Related(NetworkFlow flow) => flow.Endpoint.Protocol == protocol &&
            (flow.Endpoint.LocalPort == localPort || flow.Endpoint.LocalPort == remotePort || flow.Endpoint.RemotePort == localPort || flow.Endpoint.RemotePort == remotePort);

        internal async Task ExchangeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int sent = protocol == NetworkTransport.TCP ? 4096 : 512;
            int received = protocol == NetworkTransport.TCP ? 2048 : 256;
            if (client is not null)
            {
                await client.GetStream().WriteAsync(new byte[sent], timeout.Token);
                await server!.GetStream().ReadExactlyAsync(new byte[sent], timeout.Token);
                await server.GetStream().WriteAsync(new byte[received], timeout.Token);
                await client.GetStream().ReadExactlyAsync(new byte[received], timeout.Token);
            }
            else
            {
                await udpClient!.SendAsync(new byte[sent], new IPEndPoint(address, remotePort), timeout.Token);
                await udpServer!.ReceiveAsync(timeout.Token);
                await udpServer.SendAsync(new byte[received], new IPEndPoint(address, localPort), timeout.Token);
                await udpClient.ReceiveAsync(timeout.Token);
            }
            Sent += sent;
            Received += received;
        }

        public void Dispose()
        {
            client?.Dispose(); server?.Dispose(); listener?.Dispose(); udpClient?.Dispose(); udpServer?.Dispose();
        }
    }
}
