using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kill.Models;
using Kill.Services;

namespace Kill;

public partial class ResourceMonitorWindow : Window
{
    private readonly ResourceMonitorRepository _repository = new();
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private Task? _startTask;
    private Task? _clearTask;
    private ResourceMetricsSampler? _sampler;
    private ProcessResourceSampler? _processSampler;
    private ProcessResourceSnapshot? _latestProcessSnapshot;
    private long _activeSessionId;
    private long _sampleCount;
    private bool _hasHistory;
    private bool _storageAvailable;
    private bool _isBusy;
    private bool _isClosing;
    private bool _allowClose;

    public ResourceMonitorWindow()
    {
        InitializeComponent();
        Title = $"Kill Control {ProductInfo.DisplayVersion} - 资源监控";
        DataContext = this;
    }

    public ObservableCollection<ResourceSample> Samples { get; } = [];
    public ObservableCollection<ProcessResourceSample> TopProcesses { get; } = [];

    private bool IsMonitoring => _monitorCancellation is not null;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DatabasePathText.Text = _repository.DatabasePath;
        DatabasePathText.ToolTip = _repository.DatabasePath;
        SetBusy(true, "正在准备本地监控数据库…");
        try
        {
            var cancellationToken = _windowCancellation.Token;
            await _repository.InitializeAsync(cancellationToken);
            var recent = await _repository.LoadRecentSamplesAsync(cancellationToken: cancellationToken);
            foreach (var sample in recent) Samples.Add(sample);
            _sampleCount = await _repository.GetSampleCountAsync(cancellationToken);
            _hasHistory = await _repository.HasHistoryAsync(cancellationToken);
            _storageAvailable = true;
            UpdateRecordCount();
            UpdateTrendCharts();
            SetStatus(_sampleCount == 0 ? "数据库已就绪，尚无监控记录" : $"已读取 {_sampleCount:N0} 条本地记录");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus("无法创建资源监控数据库", true);
            MessageBox.Show(this,
                $"Kill Control 无法在程序目录创建 data 文件夹。请将程序移动到当前用户可写的目录后重试。\n\n{exception.Message}",
                "数据库不可用", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_isClosing)
            {
                _isBusy = false;
            }
            else
            {
                SetBusy(false);
                UpdateControlAvailability();
                UpdateMetricCards();
            }
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_startTask is not null) return;
        _startTask = StartMonitoringAsync();
        try
        {
            await _startTask;
        }
        finally
        {
            _startTask = null;
        }
    }

    private async Task StartMonitoringAsync()
    {
        if (_isClosing || IsMonitoring || !_storageAvailable) return;
        var metrics = GetSelectedMetrics();
        if (metrics == ResourceMetricSelection.None)
        {
            MessageBox.Show(this, "请至少选择一个监控项目。", "没有监控项目",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var interval = GetSelectedInterval();
        var duration = GetSelectedDuration();
        var options = new ResourceMonitorOptions(metrics, interval, duration);
        SetBusy(true, "正在创建监控会话…");
        try
        {
            var cancellationToken = _windowCancellation.Token;
            _activeSessionId = await _repository.StartSessionAsync(options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _hasHistory = true;
            _sampler = new ResourceMetricsSampler(metrics);
            _processSampler = new ProcessResourceSampler();
            _latestProcessSnapshot = null;
            UpdateTopProcesses();
            var monitorCancellation = new CancellationTokenSource();
            _monitorCancellation = monitorCancellation;
            SetMonitoringState(true);
            ResetCurrentValues(metrics);
            _monitorTask = Task.Run(() => MonitorLoopAsync(options, _activeSessionId, monitorCancellation));
            SetStatus(duration is null
                ? $"正在监控；每 {FormatInterval(interval)} 记录一次；不限时"
                : $"正在监控；每 {FormatInterval(interval)} 记录一次；{FormatDuration(duration.Value)}后自动停止");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
            await ResetFailedStartAsync("窗口已关闭");
        }
        catch (Exception exception)
        {
            await ResetFailedStartAsync(_isClosing ? "窗口已关闭" : "启动失败");
            if (!_isClosing)
            {
                SetStatus("无法启动资源监控", true);
                MessageBox.Show(this, exception.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (_isClosing)
            {
                _isBusy = false;
            }
            else
            {
                SetBusy(false);
                UpdateControlAvailability();
            }
        }
    }

    private async Task ResetFailedStartAsync(string reason)
    {
        _monitorCancellation?.Cancel();
        if (_monitorTask is not null)
        {
            try { await _monitorTask; }
            catch (OperationCanceledException) { }
        }

        if (_activeSessionId > 0)
        {
            try { await _repository.EndSessionAsync(_activeSessionId, reason); }
            catch { }
        }

        _activeSessionId = 0;
        _sampler?.Dispose();
        _sampler = null;
        _processSampler = null;
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        _monitorTask = null;
    }

    private async Task MonitorLoopAsync(ResourceMonitorOptions options, long sessionId,
        CancellationTokenSource monitorCancellation)
    {
        var cancellationToken = monitorCancellation.Token;
        var startedAt = Stopwatch.GetTimestamp();
        var nextSampleAt = TimeSpan.FromSeconds(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                if (ResourceMonitorTiming.HasReachedDuration(options.Duration, elapsed))
                {
                    QueueDurationStop(sessionId, monitorCancellation, options.Duration!.Value);
                    return;
                }

                var delay = ResourceMonitorTiming.GetWakeDelay(options, nextSampleAt, elapsed);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                elapsed = Stopwatch.GetElapsedTime(startedAt);
                if (ResourceMonitorTiming.HasReachedDuration(options.Duration, elapsed))
                {
                    QueueDurationStop(sessionId, monitorCancellation, options.Duration!.Value);
                    return;
                }

                var sample = _sampler?.Capture(sessionId)
                             ?? throw new InvalidOperationException("资源采样器已停止。");
                var processSnapshot = _processSampler?.Capture();
                await _repository.SaveSampleAsync(sample, cancellationToken).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    DisplaySample(sample);
                    if (processSnapshot is not null) DisplayProcessSnapshot(processSnapshot);
                });
                nextSampleAt = ResourceMonitorTiming.AdvanceSampleDeadline(nextSampleAt, options.Interval,
                    Stopwatch.GetElapsedTime(startedAt));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
                _ = HandleMonitorFailureAsync(exception, sessionId, monitorCancellation)));
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopMonitoringAsync("监控已停止");

    private async Task<bool> StopMonitoringAsync(string finalStatus, long? expectedSessionId = null,
        CancellationTokenSource? expectedCancellation = null)
    {
        await _stopGate.WaitAsync();
        var isStopping = false;
        try
        {
            if (_monitorCancellation is null ||
                expectedSessionId is not null && _activeSessionId != expectedSessionId.Value ||
                expectedCancellation is not null && !ReferenceEquals(_monitorCancellation, expectedCancellation))
                return false;

            isStopping = true;
            SetBusy(true, "正在结束监控会话…");
            _monitorCancellation.Cancel();
            if (_monitorTask is not null)
            {
                try { await _monitorTask; }
                catch (OperationCanceledException) { }
            }

            if (_activeSessionId > 0)
            {
                try { await _repository.EndSessionAsync(_activeSessionId, finalStatus); }
                catch (Exception exception)
                {
                    finalStatus += $"；会话结束时间写入失败：{exception.Message}";
                }
            }

            _monitorCancellation.Dispose();
            _monitorCancellation = null;
            _monitorTask = null;
            _sampler?.Dispose();
            _sampler = null;
            _processSampler = null;
            _activeSessionId = 0;
            SetMonitoringState(false);
            SetStatus(finalStatus, finalStatus.Contains("失败", StringComparison.Ordinal));
            return true;
        }
        finally
        {
            if (isStopping)
            {
                SetBusy(false);
                UpdateControlAvailability();
            }
            _stopGate.Release();
        }
    }

    private async Task HandleMonitorFailureAsync(Exception exception, long sessionId,
        CancellationTokenSource monitorCancellation)
    {
        if (await StopMonitoringAsync("监控因采样或写入失败而停止", sessionId, monitorCancellation) &&
            !_isClosing)
            MessageBox.Show(this, exception.Message, "资源监控已停止", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void DisplaySample(ResourceSample sample)
    {
        Samples.Insert(0, sample);
        while (Samples.Count > 200) Samples.RemoveAt(Samples.Count - 1);
        _sampleCount++;
        UpdateRecordCount();

        CpuValueText.Text = sample.CpuLabel;
        MemoryValueText.Text = sample.MemoryPercentLabel;
        MemoryDetailText.Text = sample.MemoryDetailLabel;
        DiskValueText.Text = sample.DiskActiveLabel;
        DiskDetailText.Text = $"读取 {sample.DiskReadLabel}  ·  写入 {sample.DiskWriteLabel}";
        NetworkValueText.Text = $"接收 {sample.NetworkReceivedLabel}";
        NetworkDetailText.Text = $"发送 {sample.NetworkSentLabel}";
        UpdateTrendCharts();
        SetStatus($"已记录 {sample.CapturedAtLabel} 的资源样本");
    }

    private void UpdateTrendCharts()
    {
        var chronological = GetTrendSamples();
        CpuTrendChart.SetSeries(chronological.Select(sample => sample?.CpuPercent), fixedMaximum: 100);
        MemoryTrendChart.SetSeries(chronological.Select(sample => sample?.MemoryPercent), fixedMaximum: 100);
        DiskTrendChart.SetSeries(
            chronological.Select(sample => sample?.DiskReadBytesPerSecond),
            chronological.Select(sample => sample?.DiskWriteBytesPerSecond));
        NetworkTrendChart.SetSeries(
            chronological.Select(sample => sample?.NetworkReceivedBytesPerSecond),
            chronological.Select(sample => sample?.NetworkSentBytesPerSecond));

        var latest = Samples.FirstOrDefault();
        CpuTrendValueText.Text = latest?.CpuLabel ?? "-";
        MemoryTrendValueText.Text = latest?.MemoryPercentLabel ?? "-";
        DiskTrendValueText.Text = latest is null
            ? "-"
            : $"读 {latest.DiskReadLabel}  写 {latest.DiskWriteLabel}";
        NetworkTrendValueText.Text = latest is null
            ? "-"
            : $"收 {latest.NetworkReceivedLabel}  发 {latest.NetworkSentLabel}";
    }

    private IReadOnlyList<ResourceSample?> GetTrendSamples()
    {
        var recent = Samples.Take(120).Reverse().ToArray();
        var result = new List<ResourceSample?>(recent.Length);
        ResourceSample? previous = null;
        foreach (var sample in recent)
        {
            if (previous is not null &&
                (sample.SessionId != previous.SessionId ||
                 sample.CapturedAtUtc - previous.CapturedAtUtc > TimeSpan.FromMinutes(10)))
                result.Add(null);

            result.Add(sample);
            previous = sample;
        }

        return result;
    }

    private void DisplayProcessSnapshot(ProcessResourceSnapshot snapshot)
    {
        _latestProcessSnapshot = snapshot;
        UpdateTopProcesses();
    }

    private void ProcessRankingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateTopProcesses();
    }

    private void UpdateTopProcesses()
    {
        var ranked = _latestProcessSnapshot?.GetTop(10, GetSelectedProcessSort()) ?? [];
        TopProcesses.Clear();
        foreach (var process in ranked) TopProcesses.Add(process);

        ProcessCountText.Text = _latestProcessSnapshot is null
            ? IsMonitoring ? "等待首次采样" : "尚无采样"
            : $"{_latestProcessSnapshot.Processes.Count:N0} 个进程 · " +
              $"{_latestProcessSnapshot.CapturedAtUtc.ToLocalTime():MM-dd HH:mm:ss}" +
              (IsMonitoring ? "" : " · 已停止");
        TopProcessesEmptyText.Visibility = TopProcesses.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private ProcessResourceSort GetSelectedProcessSort()
    {
        var value = (ProcessRankingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return value switch
        {
            "Memory" => ProcessResourceSort.Memory,
            "Io" => ProcessResourceSort.IoTotal,
            _ => ProcessResourceSort.Cpu
        };
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clearTask is not null) return;
        _clearTask = ClearHistoryAsync();
        try
        {
            await _clearTask;
        }
        finally
        {
            _clearTask = null;
        }
    }

    private async Task ClearHistoryAsync()
    {
        if (_isClosing || IsMonitoring || !_storageAvailable) return;
        var confirmation = new ConfirmationWindow(
            "确认清空历史数据", "永久删除全部资源监控历史数据？",
            "这会删除 SQLite 数据库中的全部监控会话和采样记录，Kill Control 不提供恢复功能。数据库文件和表结构会保留。",
            "清空", "清空历史数据", "确认文字")
        { Owner = this };
        if (confirmation.ShowDialog() != true) return;

        SetBusy(true, "正在清空本地监控记录…");
        try
        {
            await _repository.ClearAsync(_windowCancellation.Token);
            Samples.Clear();
            _sampleCount = 0;
            _hasHistory = false;
            UpdateRecordCount();
            ResetCurrentValues(GetSelectedMetrics());
            UpdateTrendCharts();
            SetStatus("资源监控记录已清空");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                SetStatus("清空监控记录失败", true);
                MessageBox.Show(this, exception.Message, "清空失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            if (_isClosing)
            {
                _isBusy = false;
            }
            else
            {
                SetBusy(false);
                UpdateControlAvailability();
            }
        }
    }

    private void OpenDataButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(KillPaths.ResourceDataRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = KillPaths.ResourceDataRoot,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开数据目录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MetricSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateMetricCards();
        UpdateControlAvailability();
    }

    private ResourceMetricSelection GetSelectedMetrics()
    {
        var metrics = ResourceMetricSelection.None;
        if (CpuCheckBox.IsChecked == true) metrics |= ResourceMetricSelection.Cpu;
        if (MemoryCheckBox.IsChecked == true) metrics |= ResourceMetricSelection.Memory;
        if (DiskCheckBox.IsChecked == true) metrics |= ResourceMetricSelection.Disk;
        if (NetworkCheckBox.IsChecked == true) metrics |= ResourceMetricSelection.Network;
        return metrics;
    }

    private TimeSpan GetSelectedInterval()
    {
        var value = (IntervalComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return TimeSpan.FromSeconds(int.TryParse(value, out var seconds) ? seconds : 5);
    }

    private TimeSpan? GetSelectedDuration()
    {
        var value = (DurationComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(value, out var minutes) && minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
    }

    private void SetMonitoringState(bool isMonitoring)
    {
        UpdateTopProcesses();
        UpdateControlAvailability();
    }

    private void UpdateControlAvailability()
    {
        var canConfigure = !_isClosing && !_isBusy && !IsMonitoring;
        CpuCheckBox.IsEnabled = canConfigure;
        MemoryCheckBox.IsEnabled = canConfigure;
        DiskCheckBox.IsEnabled = canConfigure;
        NetworkCheckBox.IsEnabled = canConfigure;
        IntervalComboBox.IsEnabled = canConfigure;
        DurationComboBox.IsEnabled = canConfigure;
        StartButton.IsEnabled = _storageAvailable && canConfigure &&
                                GetSelectedMetrics() != ResourceMetricSelection.None;
        StopButton.IsEnabled = !_isBusy && IsMonitoring;
        ClearButton.IsEnabled = _storageAvailable && canConfigure && _hasHistory;
        OpenDataButton.IsEnabled = _storageAvailable && !_isBusy;
    }

    private void UpdateMetricCards()
    {
        var metrics = GetSelectedMetrics();
        CpuCard.Opacity = metrics.HasFlag(ResourceMetricSelection.Cpu) ? 1 : 0.4;
        MemoryCard.Opacity = metrics.HasFlag(ResourceMetricSelection.Memory) ? 1 : 0.4;
        DiskCard.Opacity = metrics.HasFlag(ResourceMetricSelection.Disk) ? 1 : 0.4;
        NetworkCard.Opacity = metrics.HasFlag(ResourceMetricSelection.Network) ? 1 : 0.4;
    }

    private void ResetCurrentValues(ResourceMetricSelection metrics)
    {
        CpuValueText.Text = metrics.HasFlag(ResourceMetricSelection.Cpu) ? "等待采样" : "未选择";
        MemoryValueText.Text = metrics.HasFlag(ResourceMetricSelection.Memory) ? "等待采样" : "未选择";
        MemoryDetailText.Text = "系统物理内存";
        DiskValueText.Text = metrics.HasFlag(ResourceMetricSelection.Disk) ? "等待采样" : "未选择";
        DiskDetailText.Text = "读取 -  ·  写入 -";
        NetworkValueText.Text = metrics.HasFlag(ResourceMetricSelection.Network) ? "等待采样" : "未选择";
        NetworkDetailText.Text = "发送 -";
    }

    private void UpdateRecordCount() => RecordCountText.Text = _sampleCount <= Samples.Count
        ? $"{_sampleCount:N0} 条"
        : $"{_sampleCount:N0} 条（显示最近 {Samples.Count} 条）";

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        BusyProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) SetStatus(message);
        UpdateControlAvailability();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusDot.Fill = isError ? Brushes.IndianRed : (Brush)FindResource("TealBrush");
    }

    private static string FormatInterval(TimeSpan interval) => interval.TotalSeconds switch
    {
        < 60 => $"{interval.TotalSeconds:0} 秒",
        _ => $"{interval.TotalMinutes:0} 分钟"
    };

    private void QueueDurationStop(long sessionId, CancellationTokenSource monitorCancellation,
        TimeSpan duration) =>
        _ = Dispatcher.BeginInvoke(new Action(() =>
            _ = StopMonitoringAsync($"已达到设定时长（{FormatDuration(duration)}），监控已自动停止",
                sessionId, monitorCancellation)));

    private static string FormatDuration(TimeSpan duration) => $"{duration.TotalMinutes:0} 分钟";

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_isClosing)
        {
            e.Cancel = true;
            return;
        }

        _isClosing = true;
        _windowCancellation.Cancel();
        var startTask = _startTask;
        var clearTask = _clearTask;
        if (startTask is null && clearTask is null && !IsMonitoring)
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;
        if (startTask is not null)
        {
            try { await startTask; }
            catch { }
        }
        if (clearTask is not null)
        {
            try { await clearTask; }
            catch { }
        }
        if (IsMonitoring) await StopMonitoringAsync("窗口关闭，监控已停止");
        _allowClose = true;
        Close();
    }
}
