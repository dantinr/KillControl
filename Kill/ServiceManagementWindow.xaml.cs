using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Kill.Models;
using Kill.Services;

namespace Kill;

public partial class ServiceManagementWindow : Window
{
    private readonly ServiceDiscoveryService _discoveryService = new();
    private readonly ServiceActionCoordinator _actionCoordinator = new();
    private readonly ObservableCollection<ManagedService> _services = [];
    private ICollectionView? _servicesView;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isBusy;

    public ServiceManagementWindow() => InitializeComponent();

    private ManagedService? SelectedService => ServicesList.SelectedItem as ManagedService;

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshServicesAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshServicesAsync();

    private async Task RefreshServicesAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation = new CancellationTokenSource();
        var selectedName = SelectedService?.Name;
        SetBusy(true, "正在读取 Windows 服务…");
        try
        {
            var services = await _discoveryService.GetServicesAsync(_refreshCancellation.Token);
            _services.Clear();
            foreach (var service in services) _services.Add(service);
            ServicesList.ItemsSource = _services;
            _servicesView = CollectionViewSource.GetDefaultView(_services);
            _servicesView.Filter = FilterService;
            ApplyFilter();
            ServicesList.SelectedItem = _services.FirstOrDefault(service =>
                service.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase) && FilterService(service))
                ?? _services.FirstOrDefault(FilterService);
            SetStatus($"已读取 {_services.Count} 个服务，其中 {_services.Count(service => !service.IsProtected)} 个可管理");
        }
        catch (OperationCanceledException)
        {
            SetStatus("刷新已取消");
        }
        catch (Exception exception)
        {
            SetStatus("读取服务列表失败", true);
            MessageBox.Show(this, exception.Message, "无法读取服务", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool FilterService(object item)
    {
        if (item is not ManagedService service) return false;
        var query = SearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query) &&
            !service.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
            !service.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !service.CompanyName.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
            !service.ImageCommand.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
            !service.HostPath.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return false;

        var tag = (ServiceFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "RunningThirdParty";
        return tag switch
        {
            "RunningThirdParty" => !service.IsProtected && service.IsRunning,
            "AllThirdParty" => !service.IsProtected,
            "Blacklisted" => service.IsBlacklisted,
            _ => true
        };
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ServiceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        _servicesView?.Refresh();
        if (_servicesView is null) return;
        var visible = _servicesView.Cast<object>().Count();
        ServiceCountText.Text = visible == _services.Count ? $"共 {_services.Count} 个服务" : $"显示 {visible} / {_services.Count}";
        if (SelectedService is not null && !FilterService(SelectedService))
            ServicesList.SelectedItem = _services.FirstOrDefault(FilterService);
    }

    private void ServicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var service = SelectedService;
        EmptyState.Visibility = service is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = service is null ? Visibility.Collapsed : Visibility.Visible;
        if (service is null) return;

        ProtectionNotice.Visibility = service.IsProtected ? Visibility.Visible : Visibility.Collapsed;
        BlacklistBadge.Visibility = service.IsBlacklisted ? Visibility.Visible : Visibility.Collapsed;
        BlacklistButtonText.Text = service.IsBlacklisted ? "移出黑名单" : "列入黑名单";
        UpdateActionAvailability();
    }

    private void LocateButton_Click(object sender, RoutedEventArgs e)
    {
        var service = SelectedService;
        if (service is null) return;
        var path = new[] { service.ServiceDllPath, service.ExecutablePath, service.ProcessPath }
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "未找到可访问的宿主文件。服务命令仍显示在详情中。", "无法定位宿主",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add($"/select,{path}");
            Process.Start(startInfo);
            SetStatus($"已在文件资源管理器中定位 {Path.GetFileName(path)}");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开文件资源管理器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DisableButton_Click(object sender, RoutedEventArgs e)
    {
        var service = SelectedService;
        if (service is null || service.IsProtected) return;
        var confirmation = new ConfirmationWindow(
            "确认禁止服务运行", $"停止并禁用“{service.DisplayName}”",
            "这会把服务的启动类型改为“已禁用”并尝试立即停止。依赖此服务的软件功能可能失效；可在 Windows 服务控制台中重新启用。",
            service.Name, "禁止运行", "服务名称")
        { Owner = this };
        if (confirmation.ShowDialog() == true)
            await ExecuteActionAsync(service, ServiceActionKind.Disable);
    }

    private async void BlacklistButton_Click(object sender, RoutedEventArgs e)
    {
        var service = SelectedService;
        if (service is null || service.IsProtected) return;
        if (service.IsBlacklisted)
        {
            var confirmation = new ConfirmationWindow(
                "确认移出黑名单", $"恢复“{service.DisplayName}”的原启动方式？",
                "Kill 会恢复列入黑名单前记录的启动方式，但不会立即启动服务。软件之后可能再次启动它。",
                null, "移出黑名单", "服务名称")
            { Owner = this };
            if (confirmation.ShowDialog() == true)
                await ExecuteActionAsync(service, ServiceActionKind.RemoveFromBlacklist);
            return;
        }

        var addConfirmation = new ConfirmationWindow(
            "确认列入黑名单", $"阻止“{service.DisplayName}”再次运行",
            "Kill 会记录当前启动方式，然后停止并禁用该服务。黑名单是本机持久记录；拥有管理员权限的软件仍可能重新创建或修改服务。",
            service.Name, "列入黑名单", "服务名称")
        { Owner = this };
        if (addConfirmation.ShowDialog() == true)
            await ExecuteActionAsync(service, ServiceActionKind.AddToBlacklist);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var service = SelectedService;
        if (service is null || service.IsProtected) return;
        var confirmation = new ConfirmationWindow(
            "确认删除服务", $"永久删除“{service.DisplayName}”",
            "这是高风险操作。Kill 会先导出该服务的注册表配置，再请求 Windows 删除服务；这仍可能破坏所属软件，且备份只能用于人工恢复。建议优先使用“禁止运行”。",
            service.Name, "删除服务", "服务名称")
        { Owner = this };
        if (confirmation.ShowDialog() == true)
            await ExecuteActionAsync(service, ServiceActionKind.Delete);
    }

    private async Task ExecuteActionAsync(ManagedService service, ServiceActionKind action)
    {
        SetBusy(true, $"正在处理服务 {service.Name}…");
        try
        {
            var result = await _actionCoordinator.ExecuteAsync(service.Name, action);
            SetStatus(result.Message, !result.Success);
            var details = result.Message;
            if (!string.IsNullOrWhiteSpace(result.BackupPath)) details += $"\n\n服务配置备份：\n{result.BackupPath}";
            MessageBox.Show(this, details, result.Success ? "服务操作完成" : "服务操作未完成",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await RefreshServicesAsync();
        }
        catch (Exception exception)
        {
            SetStatus("服务操作失败", true);
            MessageBox.Show(this, exception.Message, "服务操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy, string? message = null)
    {
        _isBusy = isBusy;
        BusyProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !isBusy;
        SearchBox.IsEnabled = !isBusy;
        ServiceFilter.IsEnabled = !isBusy;
        ServicesList.IsEnabled = !isBusy;
        if (message is not null) SetStatus(message);
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        var service = SelectedService;
        LocateButton.IsEnabled = !_isBusy && service is not null;
        var canModify = !_isBusy && service is { IsProtected: false };
        DisableButton.IsEnabled = canModify;
        BlacklistButton.IsEnabled = canModify || (!_isBusy && service is { IsBlacklisted: true });
        DeleteButton.IsEnabled = canModify;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusDot.Fill = isError ? Brushes.IndianRed : (Brush)FindResource("TealBrush");
    }
}
