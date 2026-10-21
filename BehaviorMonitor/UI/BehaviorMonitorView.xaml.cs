using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using ProcessSentinel.Core;
using Activity = ProcessSentinel.Core.Activity;
using ActivityKind = ProcessSentinel.Core.ActivityKind;

namespace ProcessSentinel.App;

public partial class BehaviorMonitorView : UserControl
{
    private readonly ObservableCollection<Activity> activities = new();
    private readonly ICollectionView activityView;
    private ICollectionView? processView;
    private readonly DispatcherTimer timer;
    private MonitorClient? client;
    private bool starting, closing, preview, diagnostics;
    private readonly Stopwatch idleDuration = new();
    private Stopwatch duration => selectedSession?.Duration ?? idleDuration;

    private Window HostWindow => Window.GetWindow(this);
    public int RunningSessionCount => sessions.Count(x => x.Client.Running);
    private Task? shutdownTask;

    public BehaviorMonitorView(bool diagnosticMode = false)
    {
        InitializeComponent();
        activityView = CollectionViewSource.GetDefaultView(activities);
        activityView.Filter = MatchActivity;
        EventGrid.ItemsSource = activityView;
        monitoredProcessView = CollectionViewSource.GetDefaultView(monitoredProcesses);
        monitoredProcessView.Filter = MatchMonitoredProcess;
        MonitoredProcessGrid.ItemsSource = monitoredProcessView;
        SessionPicker.ItemsSource = sessions;
        string? settingsError = null;
        settings = diagnosticMode ? new() : AppSettings.Load(out settingsError);
        ApplySettings(settings);
        if (settingsError is not null) StatusText.Text = settingsError;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        bool initialized = false;
        Loaded += async (_, _) => { if (!initialized && !preview) { initialized = true; await RefreshProcesses(); } };
    }
    private async Task RefreshProcesses()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var selectedId = (ProcessList.SelectedItem as ProcessInfo)?.Id;
            var snapshot = await Task.Run(ProcessCatalog.Snapshot);
            processView = CollectionViewSource.GetDefaultView(snapshot.ToList());
            processView.Filter = value => value is ProcessInfo info && (string.IsNullOrWhiteSpace(ProcessSearch.Text)
                || info.Name.Contains(ProcessSearch.Text, StringComparison.OrdinalIgnoreCase)
                || info.Id.ToString().Contains(ProcessSearch.Text, StringComparison.OrdinalIgnoreCase)
                || info.Path.Contains(ProcessSearch.Text, StringComparison.OrdinalIgnoreCase));
            ProcessList.ItemsSource = processView;
            ProcessList.SelectedItem = snapshot.FirstOrDefault(x => x.Id == selectedId);
            ProcessCount.Text = $"{snapshot.Count} 个进程 · 支持名称 / PID / 路径搜索";
        }
        catch (Exception ex) { StatusText.Text = "读取进程失败：" + ex.Message; }
        finally { RefreshButton.IsEnabled = !starting && !stopping && !closing; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshProcesses();
    private void ProcessSearch_Changed(object sender, TextChangedEventArgs e) => processView?.Refresh();
    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (starting || stopping || closing) return;
        var selected = ProcessList.SelectedItem as ProcessInfo;
        SetControls(client?.Running == true);
        if (selected is null || selectedSession is not null) return;
        TargetTitle.Text = selected.Label;
        TargetPath.Text = selected.PathText;
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is ProcessInfo process && CanAddMonitor(process)) await BeginAsync(process, null);
    }
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFileDialog { Title = "选择要监控的程序", Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (pick.ShowDialog(HostWindow) != true) return;
        var options = new LaunchDialog(pick.FileName) { Owner = HostWindow };
        if (options.ShowDialog() != true) return;
        SuspendedProgram? program = null;
        try
        {
            program = SuspendedProgram.Create(pick.FileName, options.Arguments);
            await BeginAsync(ProcessCatalog.Get(program.Id), program);
        }
        catch (Exception ex) { MessageBox.Show(HostWindow, ex.Message, "无法启动程序", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { program?.Dispose(); }
    }
    private async Task<bool> BeginAsync(ProcessInfo process, SuspendedProgram? program)
    {
        if (starting || stopping || closing) return false;
        bool includeTree = IncludeChildren.IsChecked == true;
        if (FindRunningSession(process, includeTree) is { } existing)
        {
            SessionPicker.SelectedItem = existing;
            StatusText.Text = "该进程已包含在程序监控中，已切换到对应会话。";
            return true;
        }
        starting = true;
        cancelStartupRequested = false;
        startupFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MonitorSession? session = null;
        SetControls(true);
        try
        {
            StatusText.Text = "正在识别所属程序的进程树…";
            // Explicit launches start a new tree. Existing PID selection resolves its application root.
            var family = includeTree && program is null
                ? await Task.Run(() => ProcessFamilyResolver.Resolve(process, ProcessCatalog.Snapshot())) : null;
            if (closing || stopping || cancelStartupRequested) throw new OperationCanceledException();
            var request = family?.Request ?? new MonitorRequest(process, includeTree);
            if (FindRunningSession(request.Root, includeTree) is { } sameProgram)
            {
                SessionPicker.SelectedItem = sameProgram;
                return true;
            }
            if (sessions.Count(x => x.Client.Running) >= MaximumConcurrentSessions)
                throw new InvalidOperationException($"最多同时监控 {MaximumConcurrentSessions} 个程序，请先停止一个会话。");
            session = new MonitorSession(request, family?.Boundary ?? "", settings.EffectiveLogDirectory);
            startupSession = session;
            session.UpdateLabel();
            sessions.Add(session);
            SessionPicker.SelectedItem = session;
            TargetTitle.Text = session.Root.Label;
            TargetPath.Text = session.Root.PathText;
            TargetScope.Text = session.ScopeText;
            TargetScope.ToolTip = session.Boundary;
            EmptyTitle.Text = "等待采集器就绪";
            StatusText.Text = "请在 Windows UAC 提示中允许采集器运行…";
            RunBadge.Text = "●  正在连接采集器";
            await session.Client.StartAsync(session.Request);
            if (closing || stopping || cancelStartupRequested) throw new OperationCanceledException();
            program?.Resume();
            session.Duration.Restart();
            RunBadge.Text = "●  正在监控";
            StatusText.Text = "监控已开始 · 完整记录正在保存";
            ExportButton.IsEnabled = true;
            return true;
        }
        catch (Exception ex)
        {
            if (session is not null) await session.Client.StopAsync();
            string message = ex is Win32Exception { NativeErrorCode: 1223 } ? "已取消管理员授权。" : ex is OperationCanceledException ? "连接已取消或超时。" : ex.Message;
            if (session is not null) session.StartupError = message;
            StatusText.Text = message;
            RunBadge.Text = "●  未开始监控";
            if (!closing && !diagnostics) MessageBox.Show(HostWindow, message, "未开始监控", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        finally
        {
            if (session is not null) { session.Starting = false; session.UpdateLabel(); }
            startupSession = null;
            starting = false;
            startupFinished.TrySetResult();
            SetControls(client?.Running == true);
            Tick();
        }
    }
    private void SetControls(bool busy)
    {
        bool available = !starting && !stopping && !closing;
        bool capacity = sessions.Count(x => x.Client.Running) < MaximumConcurrentSessions;
        StartButton.IsEnabled = ProcessList.SelectedItem is ProcessInfo info && CanAddMonitor(info);
        LaunchButton.IsEnabled = available && capacity;
        StopButton.IsEnabled = !stopping && !closing && (starting || busy);
        StopButton.Content = starting ? "取消启动" : "停止当前";
        RefreshButton.IsEnabled = available;
        ProcessList.IsEnabled = available;
        IncludeChildren.IsEnabled = available;
        SettingsMenuItem.IsEnabled = available;
        SessionPicker.IsEnabled = available;
        UpdateSessionSummary();
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        if (starting) { cancelStartupRequested = true; startupSession?.Client.CancelStartup(); return; }
        await StopAsync();
    }
    private async Task StopAsync()
    {
        if (client is null || stopping) return;
        stopping = true;
        SetControls(false);
        try { await client.StopAsync(); Tick(); }
        finally { stopping = false; SetControls(client?.Running == true); }
    }
    private void Tick()
    {
        if (preview) return;
        foreach (var session in sessions) DrainSession(session);
        UpdateSessionSummary();
        if (client is null || selectedSession?.Starting == true) return;
        var snapshot = client.Processes;
        if (!ReferenceEquals(displayedProcessSnapshot, snapshot)) ApplyProcessSnapshot(snapshot);
        UpdateProcessSummary();
        TotalText.Text = client.Total.ToString("N0");
        TrafficText.Text = FormatBytes(client.Sent + client.Received);
        TrafficDetail.Text = $"↑ {FormatBytes(client.Sent)}   ↓ {FormatBytes(client.Received)}";
        FileText.Text = $"{client.Count(ActivityKind.File):N0} / {client.Count(ActivityKind.Registry):N0}";
        AlertText.Text = client.Alerts.ToString("N0");
        UpdateReviewSummary();
        ProcessStat.Text = $"{client.ActiveProcesses} 个存活目标进程";
        EmptyState.Visibility = activityView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = client.Total > 0 ? "没有符合筛选条件的记录" : "等待第一条行为记录";
        HistoryHint.Text = $"界面最近 {activities.Count:N0} 条 · 原始日志 {client.Total:N0} 条 · ETW 丢失 {client.EtwLost:N0} · 采集队列丢失 {client.QueueLost:N0}";
        if (client.DisplaySkipped > 0) HistoryHint.Text += $" · 界面省略 {client.DisplaySkipped:N0} 条";
        if (client.Running) StatusText.Text = $"正在监控 {duration.Elapsed.ToString(@"hh\:mm\:ss")} · {client.ActiveProcesses} 个存活进程 · 日志持续保存";
        else
        {
            duration.Stop();
            string error = selectedSession?.Error ?? client.Error;
            StatusText.Text = string.IsNullOrEmpty(error) ? $"当前监控已停止 · 日志保存于 {client.JournalPath}" : "当前采集异常 · " + error;
        }
        if (client.EtwLost + client.QueueLost > 0) StatusText.Text += " · 有事件丢失，日志不完整";
        ExportButton.IsEnabled = !string.IsNullOrEmpty(client.JournalPath);
        SetControls(client.Running);
    }

    private void AppendActivityBatch(IEnumerable<Activity> batch)
    {
        // DeferRefresh batches view settings, not ObservableCollection mutations.
        // ListCollectionView reads currency while handling each Add/Remove notification.
        foreach (var value in batch)
        {
            activities.Add(value);
            if (activities.Count > 5000) activities.RemoveAt(0);
        }
    }
    private void ResetCounters()
    {
        TotalText.Text = AlertText.Text = "0"; FileText.Text = "0 / 0"; TrafficText.Text = "0 B"; TrafficDetail.Text = "↑ 0 B   ↓ 0 B"; ProcessStat.Text = "等待采集器统计";
        DetailTitle.Text = "事件证据"; DetailText.Text = "选择一条记录，查看完整目标和触发原因。"; ExportButton.IsEnabled = false; EmptyState.Visibility = Visibility.Visible;
    }
    private bool MatchActivity(object item)
    {
        if (item is not Activity value) return false;
        int index = KindFilter?.SelectedIndex ?? 0;
        if (index > 0 && (int)value.Kind != index - 1) return false;
        if (RiskOnly?.IsChecked == true && value.Risk == RiskLevel.None) return false;
        var text = EventSearch?.Text ?? "";
        return string.IsNullOrWhiteSpace(text) || $"{value.ProcessText} {value.Target} {value.Operation} {value.Reason} {value.Detail}".Contains(text, StringComparison.OrdinalIgnoreCase);
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { activityView?.Refresh(); if (activityView is not null) EmptyState.Visibility = activityView.IsEmpty ? Visibility.Visible : Visibility.Collapsed; }
    private void EventSearch_Changed(object sender, TextChangedEventArgs e) => Filter_Changed(sender, e);
    private void EventGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReviewTab?.IsSelected == true) return;
        if (EventGrid.SelectedItem is not Activity value) return;
        ShowActivityEvidence(value);
    }
    private void ShowActivityEvidence(Activity value)
    {
        DetailTitle.Text = $"{value.KindText} / {value.Operation} · {value.RiskText}{(string.IsNullOrEmpty(value.RuleId) ? "" : " · " + value.RuleId)}";
        string traffic = value.Kind == ActivityKind.Network
            ? $"本条出网流量：{value.OutboundTrafficText}（{value.OutboundBytes:N0} 字节）\n仅计发送到公网的字节；接收、本机及局域网通信不计入。\n" : "";
        DetailText.Text = $"{value.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}   #{value.Sequence}   {value.ProcessText}\n目标：{value.Target}\n{traffic}{value.Evidence}";
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (client is null) return;
        var dialog = new SaveFileDialog { Title = "导出全部记录（含被界面省略的记录）", Filter = "原始日志 (*.jsonl)|*.jsonl|CSV 表格 (*.csv)|*.csv", FileName = "行为记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), AddExtension = true };
        if (dialog.ShowDialog(HostWindow) != true) return;
        ExportButton.IsEnabled = false;
        try
        {
            var current = client;
            string temporary = Path.Combine(Path.GetTempPath(), "ProcessSentinel-export-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                current.CopyJournal(temporary);
                await Task.Run(() =>
                {
                    if (dialog.FilterIndex == 2) EvidenceJournal.ExportCsv(temporary, dialog.FileName);
                    else File.Copy(temporary, dialog.FileName, true);
                });
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            StatusText.Text = "已导出到 " + dialog.FileName;
        }
        catch (Exception ex) { MessageBox.Show(HostWindow, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { ExportButton.IsEnabled = true; }
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = !string.IsNullOrEmpty(client?.JournalPath)
                ? Path.GetDirectoryName(client.JournalPath)! : settings.EffectiveLogDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(HostWindow, ex.Message, "无法打开日志目录", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void Help_Click(object sender, RoutedEventArgs e) => MessageBox.Show(HostWindow,
        "1. 选择任意进程后点击“添加监控”，默认自动监控所属程序的完整进程树，最多同时监控 4 个程序。\n2. 选中子进程也会纳入相关父进程、兄弟及全部子孙进程；向上追溯止于 Explorer、终端和系统公共宿主。\n3. 标题显示程序主进程，范围说明保留原始所选 PID；进程名单区分主进程、父进程和所选进程。同程序成员复用已有会话。\n4. 取消勾选自动程序树后仅监控所选 PID；选项只应用于新会话。选择 .exe 启动时，以新进程为根跟踪后代。\n5. 每个程序独立请求采集器权限、保存日志；新启动的目标使用界面当前权限。顶部切换查看时，其他程序持续监控。\n6. “停止当前”停止选中会话，“全部停止”停止所有会话，已运行的目标继续运行。已停止会话可导出或移除视图，磁盘日志保留。\n\n父链已退出、身份无法读取或 PID 复用时，在可确认位置停止追溯；范围提示可查看原因，不按名字合并所有同名程序。\n\n覆盖：文件操作、TCP/UDP 端点与传输字节、注册表、子进程、模块加载。\n限制：只记录监控开始后的事件；无法保证观察所有行为，不读取 HTTPS 内容、文件内容或注册表值，不检测内存注入。文件操作默认是请求，不能视为已成功。\n\n“需复核 / 高关注”表示行为线索，不能直接认定恶意；没有提示也不能证明安全。ETW 或队列丢失会明确显示。\n\nUAC 需要使用同一个 Windows 用户；换用其他管理员账户无法连接采集器。多个程序的子进程范围重叠时，同一事件可能分别记录到各自日志。",
        "Kill Control · 行为监控使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    public Task StopAllMonitoringAsync() => StopAllAsync();

    public Task ShutdownAsync() => shutdownTask ??= CompleteShutdownAsync();

    private async Task CompleteShutdownAsync()
    {
        closing = true;
        timer.Stop();
        SetControls(false);
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (starting)
        {
            cancelStartupRequested = true;
            startupSession?.Client.CancelStartup();
            if (startupFinished is { } completion) await completion.Task;
        }
        while (stopping) await Task.Delay(50);
        var clients = sessions.Select(x => x.Client).Distinct().ToArray();
        await Task.WhenAll(clients.Select(async x => await x.DisposeAsync()));
        sessions.Clear();
        selectedSession = null;
        client = null;
    }

    private static string FormatBytes(long bytes) => ByteSize.Format(bytes);
    public void SetPreviewData()
    {
        preview = true;
        ApplySettings(new());
        ProcessInfo[] list = [new(8420, 2400, "sample-app.exe", @"D:\Apps\Sample\sample-app.exe", 1), new(13512, 8420, "powershell.exe", "", 1), new(6100, 1, "explorer.exe", "", 1), new(9044, 1, "msedge.exe", "", 1), new(9048, 9044, "msedge.exe", "", 1), new(4500, 1, "notepad.exe", "", 1)];
        long previewStart = DateTime.UtcNow.AddMinutes(-15).Ticks;
        list[0] = list[0] with { StartTimeUtcTicks = previewStart };
        list[1] = list[1] with { StartTimeUtcTicks = previewStart + TimeSpan.TicksPerSecond };
        var previewParent = new ProcessInfo(13500, list[0].Id, "sample-helper.exe", @"D:\Apps\Sample\sample-helper.exe", previewStart + TimeSpan.TicksPerMillisecond);
        list[1] = list[1] with { ParentId = previewParent.Id, Path = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" };
        ProcessList.ItemsSource = list;
        ProcessList.SelectedIndex = 0;
        ProcessCount.Text = "示例进程 · 界面预览";
        var previewSession = new MonitorSession(new(list[0], true, list[1]), "示例：向上追溯止于 explorer.exe") { Starting = false };
        var otherPreviewSession = new MonitorSession(list[3], true) { Starting = false };
        sessions.Add(previewSession);
        sessions.Add(otherPreviewSession);
        SessionPicker.SelectedItem = previewSession;
        ProcessList.SelectedIndex = 1;
        previewSession.UpdateLabel("示例：监控中");
        otherPreviewSession.UpdateLabel("示例：监控中");
        SessionCount.Text = "示例 · 同时监控 2 个程序";
        TargetTitle.Text = "sample-app.exe · PID 8420";
        TargetPath.Text = @"D:\Apps\Sample\sample-app.exe";
        RunBadge.Text = "●  示例数据预览";
        TotalText.Text = "1,284"; TrafficText.Text = "2.4 MB"; TrafficDetail.Text = "↑ 420 KB   ↓ 2 MB"; FileText.Text = "926 / 184"; AlertText.Text = "3"; ProcessStat.Text = "3 个存活目标进程";
        ApplyProcessSnapshot([new(list[0], true, true, IsAncestor: true) { OutboundBytes = 420 * 1024 }, new(previewParent, false, true, IsAncestor: true), new(list[1], false, true, IsSelected: true) { OutboundBytes = 1536 }]);
        var samples = new[] {
            new Activity { Kind = ActivityKind.File, Operation = "读取", Target = @"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Login Data", Detail = "请求读取 4,096 字节；未确认完成状态。" },
            new Activity { Kind = ActivityKind.Network, Operation = "连接", RemoteAddress = "203.0.113.20", RemotePort = 443, Target = "192.168.1.8:52140 → 203.0.113.20:443", Detail = "TCP/IPv4 · 只记录端点，不读取通信内容。" },
            new Activity { Kind = ActivityKind.File, Operation = "写入", Target = @"D:\Apps\Sample\logs\app.log", Detail = "请求写入 512 字节；未确认完成状态。" },
            new Activity { Kind = ActivityKind.Registry, Operation = "设置值", Target = @"\REGISTRY\USER\S-1-5-21-demo\Software\Microsoft\Windows\CurrentVersion\Run\Sample", Detail = "NTSTATUS：0x00000000" },
            new Activity { Kind = ActivityKind.Process, Operation = "启动", Target = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", Detail = "父进程 PID 8420\n命令行：powershell.exe -NoProfile -EncodedCommand [示例]" },
            new Activity { Kind = ActivityKind.Image, Operation = "加载", Target = @"C:\Windows\System32\winhttp.dll", Detail = "模块大小：169,984 字节。" },
            new Activity { Kind = ActivityKind.Network, Operation = "发送", Bytes = 1460, RemoteAddress = "203.0.113.20", RemotePort = 443, Target = "192.168.1.8:52140 → 203.0.113.20:443", Detail = "TCP/IPv4 · 1,460 字节" },
            new Activity { Kind = ActivityKind.File, Operation = "打开/创建", Target = @"D:\Apps\Sample\config.json", Detail = "ETW 请求事件，未确认完成状态。" }
        };
        for (int i = 0; i < samples.Length; i++)
        {
            var item = RiskEngine.Evaluate(samples[i] with { Sequence = i + 1, Time = DateTimeOffset.Now.AddMilliseconds(i * 137), ProcessId = 8420, ProcessName = "sample-app.exe" });
            activities.Add(item);
            if (item.Risk != RiskLevel.None) previewSession.ReviewEvents.Add(item);
        }
        UpdateReviewSummary();
        EmptyState.Visibility = Visibility.Collapsed;
        EventGrid.SelectedIndex = 0;
        StopButton.IsEnabled = true;
        StopAllButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        StatusText.Text = "界面预览 · 当前显示的是示例数据，不代表任何真实程序的行为";
    }
}
