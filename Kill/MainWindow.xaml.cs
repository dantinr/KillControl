using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Kill.Models;
using Kill.Services;

namespace Kill;

public partial class MainWindow : Window
{
    private readonly ApplicationDiscoveryService _discoveryService = new();
    private readonly UninstallService _uninstallService = new();
    private readonly ResidueScanner _residueScanner = new();
    private readonly CleanupCoordinator _cleanupCoordinator = new();
    private readonly ObservableCollection<InstalledApplication> _applications = [];
    private ICollectionView? _applicationsView;
    private CancellationTokenSource? _refreshCancellation;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Kill Control {ProductInfo.DisplayVersion}";
        VersionText.Text = ProductInfo.DisplayVersion;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshApplicationsAsync();
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshApplicationsAsync();

    private async Task RefreshApplicationsAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation = new CancellationTokenSource();
        var selectedId = SelectedApplication?.Id;
        SetBusy(true, "正在读取已安装应用…");
        try
        {
            var applications = await _discoveryService.GetApplicationsAsync(_refreshCancellation.Token);
            _applications.Clear();
            foreach (var application in applications) _applications.Add(application);
            ApplicationsList.ItemsSource = _applications;
            _applicationsView = CollectionViewSource.GetDefaultView(_applications);
            _applicationsView.Filter = FilterApplication;
            ApplyFilter();
            ApplicationsList.SelectedItem = _applications.FirstOrDefault(app => app.Id == selectedId) ?? _applications.FirstOrDefault();
            SetStatus($"已读取 {_applications.Count} 个应用");
        }
        catch (OperationCanceledException)
        {
            SetStatus("刷新已取消");
        }
        catch (Exception exception)
        {
            SetStatus("读取应用列表失败", true);
            MessageBox.Show(this, exception.Message, "无法读取应用", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private InstalledApplication? SelectedApplication => ApplicationsList.SelectedItem as InstalledApplication;

    private bool FilterApplication(object item)
    {
        if (item is not InstalledApplication application) return false;
        var query = SearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query) &&
            !application.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
            !application.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
            !application.Version.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return false;

        var tag = (KindFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        return tag switch
        {
            "Desktop" => application.Kind == ApplicationKind.Desktop,
            "Store" => application.Kind == ApplicationKind.Store,
            _ => true
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        _applicationsView?.Refresh();
        if (_applicationsView is null) return;
        var visible = _applicationsView.Cast<object>().Count();
        AppCountText.Text = visible == _applications.Count ? $"共 {_applications.Count} 个应用" : $"显示 {visible} / {_applications.Count}";
    }

    private void ApplicationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedApplication;
        EmptyState.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (selected is null) return;
        UninstallButton.IsEnabled = selected.CanUninstall;
        CannotUninstallText.Visibility = selected.CanUninstall ? Visibility.Collapsed : Visibility.Visible;
        InstallDateValue.Text = string.IsNullOrWhiteSpace(selected.InstallDate) ? "未知" : selected.InstallDate;
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var application = SelectedApplication;
        if (application is null) return;

        var warning = application.IsProtected
            ? "此应用的安装位置涉及 Windows 保护区域。卸载可能影响系统组件或依赖它的其他程序。"
            : "Kill 将启动该应用自己登记的官方卸载程序。请在卸载程序中再次核对所选组件。";
        var confirmation = new ConfirmationWindow(
            "确认运行官方卸载", $"即将卸载“{application.DisplayName}”", warning,
            application.IsProtected ? application.DisplayName : null, "继续卸载")
        { Owner = this };
        if (confirmation.ShowDialog() != true) return;

        SetBusy(true, $"正在等待 {application.DisplayName} 的卸载程序…");
        UninstallButton.IsEnabled = false;
        try
        {
            var result = await _uninstallService.UninstallAsync(application);
            SetStatus(result.Message, !result.Started && !result.Cancelled);
            if (!result.Started)
            {
                MessageBox.Show(this, result.Message, "卸载未执行", MessageBoxButton.OK,
                    result.Cancelled ? MessageBoxImage.Information : MessageBoxImage.Warning);
                return;
            }

            var scanConfirmation = new ConfirmationWindow(
                "官方卸载已结束", "是否检查剩余文件和注册表项？",
                "扫描是只读操作。找到的项目会先展示，不会自动清理。", null, "开始扫描")
            { Owner = this };
            if (scanConfirmation.ShowDialog() == true) await ScanAndReviewAsync(application);
            await RefreshApplicationsAsync();
        }
        finally
        {
            SetBusy(false);
            if (SelectedApplication is not null) UninstallButton.IsEnabled = SelectedApplication.CanUninstall;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var application = SelectedApplication;
        if (application is not null) await ScanAndReviewAsync(application);
    }

    private async Task ScanAndReviewAsync(InstalledApplication application)
    {
        SetBusy(true, $"正在扫描 {application.DisplayName} 的可确认残留…");
        ScanButton.IsEnabled = false;
        try
        {
            var candidates = await _residueScanner.ScanAsync(application);
            if (candidates.Count == 0)
            {
                SetStatus("未发现可明确归属的残留");
                MessageBox.Show(this,
                    "没有发现名称和来源均能明确归属于该应用的残留。Kill 不会猜测或扩大扫描范围。",
                    "扫描完成", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var review = new CleanupReviewWindow(application.DisplayName, candidates) { Owner = this };
            if (review.ShowDialog() != true || review.SelectedCandidates.Count == 0)
            {
                SetStatus($"扫描完成，发现 {candidates.Count} 个候选项；未执行清理");
                return;
            }

            var isPermanent = review.SelectedMode == CleanupMode.PermanentDelete;
            SetBusy(true, isPermanent ? "正在直接清理所选残留…" : "正在创建备份并移入隔离区…");
            var result = isPermanent
                ? await _cleanupCoordinator.PermanentDeleteAsync(application.DisplayName, review.SelectedCandidates)
                : await _cleanupCoordinator.CleanAsync(application.DisplayName, review.SelectedCandidates);
            SetStatus(result.Message, !result.Success);
            var details = result.Errors.Count == 0 ? result.Message : result.Message + "\n\n" + string.Join("\n", result.Errors.Take(5));
            MessageBox.Show(this, details, result.Success ? (isPermanent ? "直接清理完成" : "清理完成") : "清理结果",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            SetStatus("残留扫描失败", true);
            MessageBox.Show(this, exception.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            SetBusy(false);
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e) =>
        new HistoryWindow(_cleanupCoordinator) { Owner = this }.ShowDialog();

    private void ServicesButton_Click(object sender, RoutedEventArgs e) =>
        new ServiceManagementWindow { Owner = this }.ShowDialog();

    private void ResourceMonitorButton_Click(object sender, RoutedEventArgs e) =>
        new ResourceMonitorWindow { Owner = this }.ShowDialog();

    private void SetBusy(bool isBusy, string? message = null)
    {
        BusyProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !isBusy;
        HistoryButton.IsEnabled = !isBusy;
        ServicesButton.IsEnabled = !isBusy;
        ResourceMonitorButton.IsEnabled = !isBusy;
        if (message is not null) SetStatus(message);
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusDot.Fill = isError ? Brushes.IndianRed : (Brush)FindResource("TealBrush");
    }
}
