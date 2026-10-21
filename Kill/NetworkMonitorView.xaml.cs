using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Kill.Models;
using Kill.Services;
using ProcessSentinel.Core;
using ProcessSentinel.Core.Networking;

namespace Kill;

public partial class NetworkMonitorView : WorkspaceView
{
    private readonly NetworkRows rows = [];
    private readonly Dictionary<string, NetworkFlowRow> byId = [];
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private ICollectionView? view;
    private NetworkMonitorClient? client;
    private NetworkSnapshot? latest;
    private Task? startTask, stopTask, shutdownTask;
    private bool closing, starting;
    public bool IsMonitoring => client?.Running == true;
    internal bool CanStop => starting || IsMonitoring;
    internal NetworkSnapshot? LatestSnapshot => latest;

    public NetworkMonitorView()
    {
        InitializeComponent();
        ConnectionsGrid.ItemsSource = rows;
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = MatchFilter;
        view.SortDescriptions.Add(new SortDescription(nameof(NetworkFlowRow.SendRate), ListSortDirection.Descending));
        timer.Tick += (_, _) => RefreshFromClient();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartFromShellAsync();
    internal Task StartFromShellAsync()
    {
        if (closing || starting || IsMonitoring) return startTask ?? Task.CompletedTask;
        startTask = StartCoreAsync();
        return startTask;
    }

    private async Task StartCoreAsync()
    {
        starting = true;
        SetPendingOperation(true);
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StopButton.Content = "取消启动";
        StatusText.Text = "正在等待 Windows 授权并启动网络采集…";
        var next = new NetworkMonitorClient();
        try
        {
            if (client is not null) await client.DisposeAsync();
            client = next;
            await next.StartAsync();
            if (closing) { await next.StopAsync(); return; }
            latest = null;
            RefreshFromClient();
            timer.Start();
        }
        catch (Exception exception)
        {
            await next.DisposeAsync();
            client = null;
            if (!closing) StatusText.Text = exception is OperationCanceledException or Win32Exception { NativeErrorCode: 1223 }
                ? "已取消启动，未继续采集。" : "无法启动网络监控：" + exception.Message;
        }
        finally
        {
            starting = false;
            SetPendingOperation(false);
            StopButton.Content = "停止监控";
            StopButton.IsEnabled = IsMonitoring && !closing;
            StartButton.IsEnabled = !IsMonitoring && !closing;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopFromShellAsync();
    internal Task StopFromShellAsync()
    {
        if (stopTask is { IsCompleted: false }) return stopTask;
        stopTask = StopCoreAsync();
        return stopTask;
    }

    private async Task StopCoreAsync()
    {
        if (starting)
        {
            client?.CancelStartup();
            if (startTask is not null) await startTask;
        }
        SetPendingOperation(true);
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        try
        {
            if (client is not null) await client.StopAsync();
            RefreshFromClient();
        }
        finally
        {
            timer.Stop();
            SetPendingOperation(false);
            StartButton.IsEnabled = !closing;
        }
    }

    private void RefreshFromClient()
    {
        if (client?.Snapshot is { } snapshot && !ReferenceEquals(latest, snapshot)) ApplySnapshot(snapshot);
        if (client is { Running: false } && !starting)
        {
            timer.Stop();
            StartButton.IsEnabled = !closing;
            StopButton.IsEnabled = false;
            if (!string.IsNullOrEmpty(client.Error)) StatusText.Text = client.Error;
        }
    }

    internal void ApplySnapshot(NetworkSnapshot snapshot)
    {
        latest = snapshot;
        string? selectedId = (ConnectionsGrid.SelectedItem as NetworkFlowRow)?.Id;
        var retained = snapshot.Flows.Select(flow => flow.Id).ToHashSet();
        bool membershipChanged = byId.Count != retained.Count || byId.Keys.Any(id => !retained.Contains(id));
        foreach (string id in byId.Keys.Where(id => !retained.Contains(id)).ToArray()) byId.Remove(id);
        foreach (var flow in snapshot.Flows)
        {
            if (byId.TryGetValue(flow.Id, out var row)) row.Update(flow, snapshot.CapturedAtUtc);
            else
            {
                row = new NetworkFlowRow { Flow = flow, CapturedAtUtc = snapshot.CapturedAtUtc };
                byId.Add(flow.Id, row);
            }
        }
        if (membershipChanged) rows.Replace(snapshot.Flows.Select(flow => byId[flow.Id]));
        else view!.Refresh();
        if (selectedId is not null && byId.TryGetValue(selectedId, out var selection)) ConnectionsGrid.SelectedItem = selection;
        int active = snapshot.Flows.Count(flow => flow.EndedAtUtc is null);
        int tcp = snapshot.Flows.Count(flow => flow.EndedAtUtc is null && flow.Endpoint.Protocol == NetworkTransport.TCP);
        int listening = snapshot.Flows.Count(flow => flow.EndedAtUtc is null && flow.Endpoint.State == "LISTEN");
        ConnectionCountText.Text = $"{active:N0}  ·  监听 {listening:N0}";
        ConnectionCountText.ToolTip = $"TCP：{tcp:N0} · UDP 端点/活动：{active - tcp:N0}；包含本机和局域网连接。";
        RateText.Text = $"{ByteSize.Format((long)snapshot.SendBytesPerSecond)}/s / {ByteSize.Format((long)snapshot.ReceiveBytesPerSecond)}/s";
        TrafficText.Text = $"{ByteSize.Format(snapshot.SentBytes)} / {ByteSize.Format(snapshot.ReceivedBytes)}";
        ElapsedText.Text = NetworkFlowRow.FormatDuration((snapshot.CapturedAtUtc - snapshot.StartedAtUtc).TotalSeconds);
        StatusText.Text = $"{(snapshot.Running ? "监控中" : "监控已停止")} · 更新 {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}" +
            $" · ETW 丢失 {snapshot.EventsLost:N0} · 列表截断 {snapshot.OmittedFlows:N0} 次" +
            (string.IsNullOrEmpty(snapshot.Warning) ? "" : " · " + snapshot.Warning);
        RefreshCount();
        UpdateDetails();
    }

    private bool MatchFilter(object item)
    {
        if (item is not NetworkFlowRow row) return false;
        if (ShowEndedCheck.IsChecked != true && row.IsEnded) return false;
        string protocol = (ProtocolFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        if (protocol != "All" && row.Protocol != protocol) return false;
        string query = SearchBox.Text.Trim();
        return string.IsNullOrEmpty(query) || $"{row.ProcessLabel} {row.ProcessPath} {row.LocalEndpoint} {row.RemoteEndpoint} {row.State} {row.AddressFamily}"
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (view is null) return;
        view.Refresh();
        RefreshCount();
    }

    private void RefreshCount()
    {
        int visible = view?.Cast<object>().Count() ?? 0;
        VisibleCountText.Text = $"{visible:N0} / {rows.Count:N0} 条";
        EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = latest is null ? "点击“开始监控”查看网络连接与实时流量" : "没有符合筛选条件的连接";
    }

    private void ConnectionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();
    private void UpdateDetails()
    {
        if (ConnectionsGrid.SelectedItem is NetworkFlowRow row) DetailText.Text = row.Detail;
        else DetailText.Text = "仅记录本次监控期间的传输字节，包含本机和局域网流量。观测时长从首次发现连接开始，不等于程序运行时长。UDP 端点没有固定远端，发生传输后按远端分别显示。";
    }

    internal Task ShutdownAsync() => shutdownTask ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        closing = true;
        if (starting) client?.CancelStartup();
        await StopFromShellAsync();
        if (client is not null) { await client.DisposeAsync(); client = null; }
        timer.Stop();
    }

    private sealed class NetworkRows : ObservableCollection<NetworkFlowRow>
    {
        internal void Replace(IEnumerable<NetworkFlowRow> values)
        {
            Items.Clear();
            foreach (var value in values) Items.Add(value);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
