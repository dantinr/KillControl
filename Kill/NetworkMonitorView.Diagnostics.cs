using System.ComponentModel;
using System.Windows.Controls;
using Kill.Models;
using ProcessSentinel.Core;
using ProcessSentinel.Core.Networking;

namespace Kill;

public partial class NetworkMonitorView
{
    internal NetworkMonitorClientDiagnostic Diagnostic => new(client?.Running == true, client?.Error ?? StatusText.Text);
    internal sealed record NetworkMonitorClientDiagnostic(bool Running, string Error);

    internal void SetPreviewData()
    {
        var t = DateTimeOffset.UtcNow;
        var process = new ProcessInfo(12345, 1, "示例应用（演示数据，长进程名称）", @"C:\Example\Application With A Long Directory Name\Nested Application Folder\bin\app.exe", t.AddMinutes(-10).UtcTicks);
        NetworkFlow Flow(string id, NetworkTransport protocol, string local, int localPort, string remote, int remotePort, string state,
            long sent, long received, double sendRate, double receiveRate, bool ended = false) =>
            new(id, new(protocol, process.Id, local, localPort, remote, remotePort, state), process,
                t.AddMinutes(-3), t.AddSeconds(-1), ended ? t.AddMinutes(-1) : null, sent, received, sendRate, receiveRate);
        ApplySnapshot(new(t.AddMinutes(-5), t, true,
            [Flow("tcp", NetworkTransport.TCP, "192.0.2.10", 52341, "203.0.113.20", 443, "ESTABLISHED", 300_000, 12_000_000, 32_768, 524_288),
             Flow("ipv6", NetworkTransport.TCP, "2001:db8:1234:5678::1234", 53210, "2001:db8:abcd:1234::5678", 443, "ESTABLISHED", 14_000, 900_000, 1024, 24_576),
             Flow("udp", NetworkTransport.UDP, "192.0.2.10", 60321, "192.0.2.1", 53, "UDP_ACTIVE", 300, 1200, 120, 450),
             Flow("listen", NetworkTransport.TCP, "0.0.0.0", 8080, "", 0, "LISTEN", 0, 0, 0, 0),
             Flow("closed", NetworkTransport.TCP, "192.0.2.10", 51341, "203.0.113.21", 443, "DISCONNECTED", 1500, 19_000, 0, 0, true)],
            315_800, 12_920_200, 33_912, 549_314));
        ConnectionsGrid.SelectedItem = byId["ipv6"];
        StatusText.Text = "演示数据 · 实际采集仅在点击“开始监控”后运行";
    }

    internal void VerifyUi(Action<string> report)
    {
        Check(!IsMonitoring && client is null, "opening the network page does not start collection or elevation");
        SetPreviewData();
        Check(ConnectionsGrid.Items.Count == 5 && ConnectionCountText.Text.StartsWith("4"), "network summary includes active endpoints and retains closed flows");
        var selected = byId["tcp"];
        ConnectionsGrid.SelectedItem = selected;
        var snapshot = latest!;
        ApplySnapshot(snapshot with
        {
            CapturedAtUtc = snapshot.CapturedAtUtc.AddSeconds(2),
            Flows = snapshot.Flows.Select(flow => flow.Id == "udp" ? flow with { SendBytesPerSecond = 999_999 } : flow).ToArray()
        });
        Check(ReferenceEquals(selected, ConnectionsGrid.SelectedItem) && ((NetworkFlowRow)ConnectionsGrid.Items[0]).Id == "udp",
            "network updates preserve selection and refresh numeric rate sorting");
        SearchBox.Text = "2001:db8";
        Check(ConnectionsGrid.Items.Count == 1 && ((NetworkFlowRow)ConnectionsGrid.Items[0]).AddressFamily == "IPv6", "network search finds IPv6 endpoints");
        SearchBox.Text = "";
        ProtocolFilter.SelectedIndex = 2;
        Check(ConnectionsGrid.Items.Count == 1 && ((NetworkFlowRow)ConnectionsGrid.Items[0]).Protocol == "UDP", "network protocol filter selects UDP");
        ProtocolFilter.SelectedIndex = 0;
        ShowEndedCheck.IsChecked = false;
        Check(ConnectionsGrid.Items.Count == 4, "network filter can hide ended flows");
        ShowEndedCheck.IsChecked = true;
        var ended = byId["closed"];
        double duration = ended.DurationSeconds;
        ApplySnapshot(latest! with { CapturedAtUtc = latest.CapturedAtUtc.AddSeconds(10) });
        Check(ended.DurationSeconds == duration && byId["tcp"].DurationSeconds > duration,
            "ended network durations freeze while active observations continue");
        Check(ConnectionsGrid.Columns.First(column => column.Header.ToString() == "累计发送").SortMemberPath == nameof(NetworkFlowRow.SentBytes),
            "network byte columns sort by numeric values");
        view!.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(NetworkFlowRow.SendRate), ListSortDirection.Descending));

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            report("PASS: " + description);
        }
    }
}
