using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ProcessSentinel.Core;

namespace ProcessSentinel.App;

public partial class BehaviorMonitorView
{
    private readonly ObservableCollection<TrackedProcess> monitoredProcesses = new();
    private readonly ICollectionView monitoredProcessView;
    private IReadOnlyList<TrackedProcess>? displayedProcessSnapshot;

    private void ApplyProcessSnapshot(IReadOnlyList<TrackedProcess> snapshot)
    {
        var selected = MonitoredProcessGrid.SelectedItem as TrackedProcess;
        var incoming = snapshot.ToDictionary(x => (x.Process.Id, x.Process.StartTimeUtcTicks));
        var retained = new HashSet<(int, long)>();
        // Mutate the bound collection directly; do not defer refresh during notifications.
        for (int i = monitoredProcesses.Count - 1; i >= 0; i--)
        {
            var current = monitoredProcesses[i];
            var identity = (current.Process.Id, current.Process.StartTimeUtcTicks);
            if (!incoming.TryGetValue(identity, out var next)) monitoredProcesses.RemoveAt(i);
            else
            {
                retained.Add(identity);
                if (current != next) monitoredProcesses[i] = next;
            }
        }
        foreach (var process in snapshot)
            if (!retained.Contains((process.Process.Id, process.Process.StartTimeUtcTicks))) monitoredProcesses.Add(process);
        if (selected is not null)
        {
            var replacement = monitoredProcesses.FirstOrDefault(x => x.Process.Id == selected.Process.Id
                && x.Process.StartTimeUtcTicks == selected.Process.StartTimeUtcTicks);
            if (replacement is not null && monitoredProcessView.Contains(replacement)) MonitoredProcessGrid.SelectedItem = replacement;
        }
        displayedProcessSnapshot = snapshot;
        UpdateProcessSummary();
        if (MonitorTabs.SelectedIndex == 1) ShowSelectedProcess();
    }

    private void UpdateProcessSummary()
    {
        int active = monitoredProcesses.Count(x => x.IsRunning);
        MonitoredProcessTab.Header = $"监控进程（{active}）";
        if (monitoredProcesses.Count == 0)
            MonitoredProcessCount.Text = "开始监控后显示目标及其全部被跟踪子进程";
        else
            MonitoredProcessCount.Text = $"{(!preview && client?.Running == false ? "监控已停止 · 最后记录：" : "")}存活 {active} · 已退出 {monitoredProcesses.Count - active} · 共 {monitoredProcesses.Count} 个进程";
    }

    private void ClearMonitoredProcesses()
    {
        monitoredProcesses.Clear();
        displayedProcessSnapshot = null;
        MonitoredProcessSearch.Clear();
        LiveProcessesOnly.IsChecked = false;
        UpdateProcessSummary();
    }

    private bool MatchMonitoredProcess(object value)
    {
        if (value is not TrackedProcess item) return false;
        if (LiveProcessesOnly?.IsChecked == true && !item.IsRunning) return false;
        string text = MonitoredProcessSearch?.Text ?? "";
        return string.IsNullOrWhiteSpace(text) || $"{item.Process.Name} {item.Process.Id} {item.Process.ParentId} {item.Process.Path}"
            .Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void MonitoredProcessFilter_Changed(object sender, RoutedEventArgs e) => monitoredProcessView?.Refresh();
    private void MonitoredProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorTabs.SelectedIndex == 1) ShowSelectedProcess();
    }
    private void MonitorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MonitorTabs) || EventGrid is null || MonitoredProcessGrid is null) return;
        if (MonitorTabs.SelectedIndex == 1) ShowSelectedProcess();
        else if (ReviewTab?.IsSelected == true) ReviewGrid_SelectionChanged(sender, e);
        else EventGrid_SelectionChanged(sender, e);
    }
    private void ShowSelectedProcess()
    {
        DetailTitle.Text = "进程信息";
        if (MonitoredProcessGrid.SelectedItem is not TrackedProcess value)
        {
            DetailText.Text = "选择一个被监控进程，查看完整路径、父 PID、启动时间和最后运行状态。";
            return;
        }
        var process = value.Process;
        DetailText.Text = $"{process.Label}\n角色：{value.RoleText}   父 PID：{process.ParentId}   {(!preview && client?.Running == false ? "最后状态" : "状态")}：{value.StateText}\n出网流量：{value.OutboundTrafficText}（{value.OutboundBytes:N0} 字节）\n仅本进程自监控开始发送到公网的累计字节；不含接收、本机及局域网通信。\n启动时间：{process.StartTimeText}\n程序路径：{value.PathText}";
    }
}
