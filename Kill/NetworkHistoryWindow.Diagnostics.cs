using System.Windows;
using Kill.Models;
using ProcessSentinel.Core.Networking;

namespace Kill;

public partial class NetworkHistoryWindow
{
    internal void SetPreviewData()
    {
        preview = true;
        var t = DateTimeOffset.UtcNow;
        var process = new NetworkHistoryProcess(1, new("preview", 12345, t.AddHours(-1).UtcTicks), "示例应用（演示数据）",
            @"C:\Example\Application With A Long Directory Name\bin\app.exe", t.AddMinutes(-20), t, 3);
        ProcessesGrid.ItemsSource = new[] { process, process with { Id = 2, Name = "另一个进程（演示数据）", Key = new("preview", 23456, t.AddHours(-2).UtcTicks), ConnectionCount = 1 } };
        ProcessesGrid.SelectedItem = process;
        HistoryGrid.ItemsSource = new[]
        {
            new NetworkHistoryConnection("v6", "TCP", "2001:db8:1234:5678::1234", 53210, "2001:db8:abcd:1234::5678", 443,
                "DISCONNECTED", t.AddMinutes(-10), t.AddMinutes(-2), t.AddMinutes(-2), t.AddMinutes(-2), 24000, 1200000),
            new NetworkHistoryConnection("v4", "TCP", "192.0.2.10", 52100, "203.0.113.20", 443,
                "MONITOR_STOPPED", t.AddMinutes(-20), t, t.AddSeconds(-2), t, 123456, 2400000),
            new NetworkHistoryConnection("udp", "UDP", "192.0.2.10", 60210, "192.0.2.1", 53,
                "MONITOR_STOPPED", t.AddMinutes(-5), t, t.AddMinutes(-1), t, 800, 2400)
        };
        HistoryGrid.SelectedIndex = 0;
        ProcessPageText.Text = "1–2 / 2 个进程";
        ConnectionPageText.Text = "1–3 / 3 条连接";
        EmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = "演示数据 · 实际记录由网络监控自动保存到本机";
    }

    internal async Task VerifyUiAsync(Action<string> report)
    {
        await WhenIdle;
        Check(ProcessesGrid.Items.Count == 3 && HistoryGrid.Items.Count == 4 &&
            (ProcessesGrid.SelectedItem as NetworkHistoryProcess)?.Key == preferred,
            "history opens on the selected process instance and excludes listeners");
        Check(HistoryGrid.Items.Cast<NetworkHistoryConnection>().Any(row => row.RemoteAddress.Contains(':') && row.RemotePort == 443),
            "history shows IPv6 remote addresses and numeric ports");
        RemoteSearchBox.Text = "2001:db8";
        await SearchConnectionsAsync();
        Check(HistoryGrid.Items.Count == 1 && DetailsText.Text.Contains("2001:db8"), "history remote search updates rows and full endpoint details");
        RemoteSearchBox.Text = "";
        await SearchConnectionsAsync();
        ProcessesGrid.SelectedItem = ProcessesGrid.Items.Cast<NetworkHistoryProcess>().First(row => row.Key != preferred);
        await WhenIdle;
        Check(HistoryGrid.Items.Count == 1, "switching historical process instances isolates their endpoints");
        await SelectProcessAsync(preferred!);
        Check(HistoryGrid.Items.Count == 4 && (ProcessesGrid.SelectedItem as NetworkHistoryProcess)?.Key == preferred,
            "an already open history window can focus another selected process");
        ProcessSearchBox.Text = "SecondHistory";
        await SearchProcessesAsync();
        Check(ProcessesGrid.Items.Count == 1 && HistoryGrid.Items.Count == 1, "history supports process name searches");
        ProcessSearchBox.Text = "no matching process";
        await SearchProcessesAsync();
        Check(ProcessesGrid.Items.Count == 0 && HistoryGrid.Items.Count == 0 && EmptyText.Visibility == Visibility.Visible,
            "history clears stale process and connection details for empty searches");
        ProcessSearchBox.Text = "";
        await SearchProcessesAsync();
        Check(ProcessesGrid.Items.Count == 3 && HistoryGrid.Items.Count == 4, "history refresh restores saved records without restarting collection");

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            report("PASS: " + description);
        }
    }

    internal void VerifyMissingProcess(Action<string> report)
    {
        if (ProcessesGrid.SelectedItem is not null || HistoryGrid.Items.Count != 0)
            throw new InvalidOperationException("A missing process history displayed another process's endpoints.");
        report("PASS: a process with no saved history does not display another process's endpoints");
    }
}
