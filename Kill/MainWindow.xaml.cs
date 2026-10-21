using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kill.Services;
using ProcessSentinel.App;

namespace Kill;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _views = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly bool _diagnostics;
    private readonly string? _resourceDatabasePath;
    private bool _closing;
    private bool _allowClose;
    private ResourceMonitorView? ResourceMonitor => _views.GetValueOrDefault("Resources") as ResourceMonitorView;
    private BehaviorMonitorView? BehaviorMonitor => _views.GetValueOrDefault("Behavior") as BehaviorMonitorView;

    public MainWindow(string initialModule = "Applications", bool diagnostics = false, string? resourceDatabasePath = null)
    {
        _diagnostics = diagnostics;
        _resourceDatabasePath = resourceDatabasePath;
        InitializeComponent();
        Title = $"Kill Control {ProductInfo.DisplayVersion}";
        VersionText.Text = ProductInfo.DisplayVersion;
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => UpdateMonitoringStatus(), Dispatcher);
        Navigate(initialModule);
    }

    internal FrameworkElement Navigate(string module)
    {
        var tab = module switch
        {
            "Services" => ServicesTab,
            "Resources" => ResourcesTab,
            "Behavior" => BehaviorTab,
            "History" => HistoryTab,
            _ => ApplicationsTab
        };
        module = (string)tab.Tag;
        if (tab.IsChecked != true) tab.IsChecked = true;
        if (!_views.TryGetValue(module, out var view))
        {
            view = module switch
            {
                "Services" => new ServiceManagementView(),
                "Resources" => new ResourceMonitorView(_resourceDatabasePath),
                "Behavior" => new BehaviorMonitorView(_diagnostics),
                "History" => new HistoryView(new CleanupCoordinator()),
                _ => new ApplicationsView()
            };
            _views.Add(module, view);
            Workspace.Children.Add(view);
        }
        foreach (var page in _views.Values) page.Visibility = ReferenceEquals(page, view) ? Visibility.Visible : Visibility.Collapsed;
        if (view is HistoryView history) history.RefreshHistory();
        return view;
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string module } && Workspace is not null) Navigate(module);
    }

    private void UpdateMonitoringStatus()
    {
        int behaviorCount = BehaviorMonitor?.RunningSessionCount ?? 0;
        bool resourcesRunning = ResourceMonitor?.IsMonitoring == true;
        MonitoringStatus.Text = $"资源：{(resourcesRunning ? "监控中" : "未开始")}   ·   行为：{behaviorCount} 个程序";
        StopMonitoringButton.IsEnabled = !_closing && (resourcesRunning || behaviorCount > 0);
    }

    private async void StopMonitoring_Click(object sender, RoutedEventArgs e)
    {
        StopMonitoringButton.IsEnabled = false;
        try
        {
            if (BehaviorMonitor is { } behavior) await behavior.StopAllMonitoringAsync();
            if (ResourceMonitor is { } resources) await resources.StopFromShellAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "停止监控", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { UpdateMonitoringStatus(); }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _statusTimer.Stop();
        IsEnabled = false;
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            var cleanupTasks = _views.Values.OfType<WorkspaceView>().Select(view => view.WhenIdle).ToList();
            if (_views.GetValueOrDefault("Applications") is ApplicationsView applications) cleanupTasks.Add(applications.ShutdownAsync());
            if (BehaviorMonitor is { } behavior) cleanupTasks.Add(behavior.ShutdownAsync());
            if (ResourceMonitor is { } resources) cleanupTasks.Add(resources.ShutdownAsync());
            await Task.WhenAll(cleanupTasks);
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _closing = false;
            IsEnabled = true;
            _statusTimer.Start();
            MessageBox.Show(this, "监控尚未完整停止，请重试关闭。\n\n" + exception.Message,
                "关闭未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal async Task CloseForDiagnosticsAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => completion.TrySetResult();
        Close();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
