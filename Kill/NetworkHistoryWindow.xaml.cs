using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kill.Models;
using Kill.Services;

namespace Kill;

public partial class NetworkHistoryWindow : Window
{
    private const int PageSize = 100;
    private readonly NetworkHistoryRepository repository;
    private NetworkHistoryProcessKey? preferred;
    private readonly CancellationTokenSource lifetime = new();
    private Task pending = Task.CompletedTask;
    private int processOffset, connectionOffset;
    private bool loaded, closed, settingSelection, preview, initialSelectionApplied;
    internal Task WhenIdle => pending;

    public NetworkHistoryWindow(NetworkHistoryRepository repository, NetworkHistoryProcessKey? preferred = null)
    {
        this.repository = repository;
        this.preferred = preferred;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded || preview) return;
        loaded = true;
        await ReloadAsync();
    }

    internal Task ReloadAsync() => RunAsync(() => LoadProcessesAsync());
    internal async Task SelectProcessAsync(NetworkHistoryProcessKey key)
    {
        await WhenIdle;
        if (closed) return;
        await RunAsync(async () =>
        {
            preferred = key;
            initialSelectionApplied = false;
            processOffset = 0;
            ProcessSearchBox.Text = RemoteSearchBox.Text = "";
            await LoadProcessesAsync();
        });
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void ProcessSearch_Click(object sender, RoutedEventArgs e) => await SearchProcessesAsync();
    private Task SearchProcessesAsync() => RunAsync(async () => { processOffset = 0; await LoadProcessesAsync(); });
    private async void ProcessSearch_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await SearchProcessesAsync(); } }
    private async void RemoteSearch_Click(object sender, RoutedEventArgs e) => await SearchConnectionsAsync();
    private Task SearchConnectionsAsync() => RunAsync(async () => { connectionOffset = 0; await LoadConnectionsAsync(); });
    private async void RemoteSearch_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await SearchConnectionsAsync(); } }

    private async Task LoadProcessesAsync()
    {
        long? selected = (ProcessesGrid.SelectedItem as NetworkHistoryProcess)?.Id;
        var page = await repository.LoadProcessesAsync(ProcessSearchBox.Text, processOffset, PageSize, preferred, lifetime.Token);
        if (closed) return;
        settingSelection = true;
        try
        {
            ProcessesGrid.ItemsSource = page.Rows;
            ProcessesGrid.SelectedItem = !initialSelectionApplied && preferred is not null
                ? page.Rows.FirstOrDefault(row => row.Key == preferred)
                : page.Rows.FirstOrDefault(row => row.Id == selected) ?? page.Rows.FirstOrDefault();
            initialSelectionApplied = true;
        }
        finally { settingSelection = false; }
        ProcessPageText.Text = PageLabel(processOffset, page.Rows.Count, page.TotalCount, "个进程");
        ProcessPrevious.IsEnabled = processOffset > 0;
        ProcessNext.IsEnabled = processOffset + page.Rows.Count < page.TotalCount;
        connectionOffset = 0;
        await LoadConnectionsAsync();
    }

    private async Task LoadConnectionsAsync()
    {
        var selected = ProcessesGrid.SelectedItem as NetworkHistoryProcess;
        var page = selected is null ? new NetworkHistoryPage<NetworkHistoryConnection>([], 0)
            : await repository.LoadConnectionsAsync(selected.Id, RemoteSearchBox.Text, connectionOffset, PageSize, lifetime.Token);
        if (closed) return;
        HistoryGrid.ItemsSource = page.Rows;
        HistoryGrid.SelectedItem = page.Rows.FirstOrDefault();
        ConnectionPageText.Text = PageLabel(connectionOffset, page.Rows.Count, page.TotalCount, "条连接");
        ConnectionPrevious.IsEnabled = connectionOffset > 0;
        ConnectionNext.IsEnabled = connectionOffset + page.Rows.Count < page.TotalCount;
        EmptyText.Visibility = page.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = selected is null
            ? preferred is not null ? "所选进程暂无已保存的远端连接，请在左侧选择其他进程。" : "暂无进程历史。开始网络监控后，远端连接会自动保存。"
            : "没有符合筛选条件的连接历史。";
        UpdateDetails();
    }

    private async void Process_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingSelection || preview) return;
        await RunAsync(async () => { connectionOffset = 0; await LoadConnectionsAsync(); });
    }
    private void Connection_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();
    private void UpdateDetails()
    {
        string process = (ProcessesGrid.SelectedItem as NetworkHistoryProcess)?.Detail ?? "选择左侧进程查看连接历史。";
        DetailsText.Text = process + (HistoryGrid.SelectedItem is NetworkHistoryConnection row ? "\n" + row.Detail : "");
    }

    private async void ProcessPrevious_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { processOffset = Math.Max(0, processOffset - PageSize); await LoadProcessesAsync(); });
    private async void ProcessNext_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { processOffset += PageSize; await LoadProcessesAsync(); });
    private async void ConnectionPrevious_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { connectionOffset = Math.Max(0, connectionOffset - PageSize); await LoadConnectionsAsync(); });
    private async void ConnectionNext_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    { connectionOffset += PageSize; await LoadConnectionsAsync(); });

    private Task RunAsync(Func<Task> action)
    {
        if (closed || !pending.IsCompleted) return pending;
        pending = RunCoreAsync(action);
        return pending;
    }
    private async Task RunCoreAsync(Func<Task> action)
    {
        HistoryContent.IsEnabled = RefreshButton.IsEnabled = false;
        StatusText.Text = "正在读取历史…";
        try
        {
            await action();
            if (!closed) StatusText.Text = "本地保存：" + repository.DatabasePath + " · 点击刷新读取最新记录 · 停止或重启后仍可查看";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (!closed) StatusText.Text = "历史读取失败：" + exception.Message; }
        finally { if (!closed) HistoryContent.IsEnabled = RefreshButton.IsEnabled = true; }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        _ = pending.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
    private static string PageLabel(int offset, int count, long total, string unit) =>
        count == 0 ? $"0 / {total:N0} {unit}" : $"{offset + 1:N0}–{offset + count:N0} / {total:N0} {unit}";
}
