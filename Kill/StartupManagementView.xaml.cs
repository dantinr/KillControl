using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Kill.Models;
using Kill.Services;

namespace Kill;

public partial class StartupManagementView : WorkspaceView
{
    private IReadOnlyList<StartupEntry> _entries = [];
    private bool _loaded;
    private bool _busy;
    private bool _preview;
    private StartupEntry? Selected => EntriesGrid.SelectedItem as StartupEntry;

    public StartupManagementView() => InitializeComponent();

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _preview) return;
        _loaded = true;
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync(bool alreadyBusy = false)
    {
        if (_busy && !alreadyBusy) return;
        string? selectedId = Selected?.Id;
        if (!alreadyBusy) SetBusy(true);
        try
        {
            var snapshot = await Task.Run(() => new StartupDiscoveryService().Discover());
            _entries = snapshot.Entries;
            ApplyFilter(selectedId);
            StatusText.Text = snapshot.Warnings.Count == 0 ? "已读取。已注册表示启动项存在；Windows 或应用策略仍可能限制启动。" : $"读取完成，有 {snapshot.Warnings.Count} 项读取问题（悬停查看）。";
            StatusText.ToolTip = snapshot.Warnings.Count > 0 ? string.Join("\n", snapshot.Warnings) : StatusText.Text;
        }
        catch (Exception exception) { StatusText.Text = "无法读取启动项：" + exception.Message; StatusText.ToolTip = StatusText.Text; }
        finally { if (!alreadyBusy) SetBusy(false); }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid is not null && StatusFilter is not null && ScopeFilter is not null) ApplyFilter(Selected?.Id);
    }

    private void ApplyFilter(string? selectedId = null)
    {
        string query = SearchBox.Text.Trim();
        var rows = _entries.Where(entry => (query.Length == 0 || $"{entry.Name} {entry.Command} {entry.Location} {entry.SourceLabel}".Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (ScopeFilter.SelectedIndex switch { 1 => !entry.IsMachine, 2 => entry.IsMachine, _ => true }) &&
            (StatusFilter.SelectedIndex switch
            {
                1 => !entry.IsDisabled && !entry.HasConflict && entry.Approval == StartupApproval.Registered,
                2 => entry.IsDisabled || entry.Approval == StartupApproval.WindowsDisabled,
                3 => entry.HasConflict || entry.Approval == StartupApproval.Unknown || entry.ProtectionReason.Length > 0,
                _ => true
            })).ToArray();
        EntriesGrid.ItemsSource = rows;
        EntriesGrid.SelectedItem = rows.FirstOrDefault(entry => entry.Id == selectedId) ?? rows.FirstOrDefault();
        CountText.Text = $"显示 {rows.Length} / {_entries.Count} 项 · Kill 禁用 {_entries.Count(entry => entry.IsDisabled)} 项";
        if (rows.Length == 0) EmptyText.Text = "没有符合条件的启动项。";
        UpdateSelection();
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var entry = Selected;
        DetailPanel.DataContext = entry;
        DetailPanel.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        DisableButton.IsEnabled = !_busy && entry?.CanDisable == true;
        RestoreButton.IsEnabled = !_busy && entry?.CanRestore == true;
        DisableButton.Visibility = entry?.IsDisabled == true ? Visibility.Collapsed : Visibility.Visible;
        RestoreButton.Visibility = entry?.IsDisabled == true ? Visibility.Visible : Visibility.Collapsed;
        LocateButton.IsEnabled = entry is not null && FindFile(entry).Length > 0;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SetPendingOperation(busy);
        RefreshButton.IsEnabled = !busy;
        EntriesGrid.IsEnabled = !busy;
        UpdateSelection();
    }

    private async void Disable_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(false);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(true);

    private async Task ExecuteAsync(bool restore)
    {
        if (_busy || _preview || Selected is not { } entry || (restore ? !entry.CanRestore : !entry.CanDisable)) return;
        string action = restore ? "恢复" : "禁用";
        string description = $"{action}启动项：{entry.Name}\n\n来源：{entry.Location}\n\n{entry.ActionNote}" +
            (entry.IsMachine ? "\n\n此项影响所有用户，需要 Windows 管理员授权。" : "");
        if (new ConfirmationWindow(action + "启动项", action + "此启动项？", description, null, "确认" + action) { Owner = HostWindow }.ShowDialog() != true) return;
        SetBusy(true);
        try
        {
            var result = await new StartupActionCoordinator().ExecuteAsync(entry, restore);
            await RefreshAsync(true);
            StatusText.Text = result.Message;
            StatusText.ToolTip = result.Message;
            if (!result.Success) MessageBox.Show(HostWindow, result.Message, "启动项管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception) { MessageBox.Show(HostWindow, exception.Message, "启动项管理", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }

    private static string FindFile(StartupEntry entry)
    {
        string file = entry.IsFolder ? entry.IsDisabled ? StartupPolicy.PayloadPath(entry) : entry.Location : StartupPolicy.ResolveExecutable(entry.Command);
        return File.Exists(file) ? file : "";
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry || FindFile(entry) is not { Length: > 0 } file) return;
        Launch(() =>
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(file);
            Process.Start(start);
        });
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Launch(() => Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }));

    private void Launch(Action action)
    {
        try { action(); }
        catch (Exception exception) { MessageBox.Show(HostWindow, exception.Message, "打开位置", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal void SetPreviewData()
    {
        _preview = true;
        _entries =
        [
            new() { Source = StartupSource.UserRun, Name = "示例同步工具（较长的启动项名称用于验证布局）", Command = "\"C:\\Program Files\\Example Sync Tool\\Example.Sync.Desktop.exe\" --background --profile \"C:\\Users\\Example\\Documents\\同步配置\\personal.json\"" },
            new() { Source = StartupSource.MachineRun64, Name = "Example Update", Command = @"C:\Tools\Updater.exe /startup", IsDisabled = true },
            new() { Source = StartupSource.UserFolder, Name = "工作助手.lnk", Command = @"C:\Users\Example\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\工作助手.lnk" },
            new() { Source = StartupSource.UserRun, Name = "Example Chat", Command = @"C:\Tools\Chat.exe --autostart", Approval = StartupApproval.WindowsDisabled },
            new() { Source = StartupSource.MachineRun64, Name = "Windows 系统通知", Command = @"C:\Windows\System32\SecurityHealthSystray.exe", ProtectionReason = "Windows 系统组件，仅供查看。" },
            new() { Source = StartupSource.UserRun, Name = "旧版工具（原位置出现同名项目）", Command = @"C:\Tools\Old.exe", HasConflict = true }
        ];
        ApplyFilter();
        StatusText.Text = "预览数据 · 操作不会修改电脑的启动配置。";
    }

    internal void VerifyUi(Action<string> report)
    {
        SetPreviewData();
        Check(EntriesGrid.Items.Count == 6 && DisableButton.IsEnabled, "startup registrations and details are displayed");
        EntriesGrid.SelectedIndex = 1;
        Check(RestoreButton.IsEnabled && DisableButton.Visibility == Visibility.Collapsed, "Kill-disabled startup entries offer restoration");
        EntriesGrid.SelectedIndex = 3;
        Check(!DisableButton.IsEnabled && !RestoreButton.IsEnabled, "Windows-disabled entries use the Windows settings route");
        EntriesGrid.SelectedIndex = 4;
        Check(!DisableButton.IsEnabled, "system startup components remain read-only");
        EntriesGrid.SelectedIndex = 5;
        Check(!DisableButton.IsEnabled && !RestoreButton.IsEnabled, "startup backup conflicts cannot overwrite a live item");
        SearchBox.Text = "--profile";
        Check(EntriesGrid.Items.Count == 1, "startup search includes complete command arguments");
        SearchBox.Text = "";
        StatusFilter.SelectedIndex = 2;
        Check(EntriesGrid.Items.Count == 2, "startup disabled filter includes Kill and Windows disabled states");
        StatusFilter.SelectedIndex = 0;
        ScopeFilter.SelectedIndex = 2;
        Check(EntriesGrid.Items.Count == 2, "startup scope filter separates machine and current-user entries");
        ScopeFilter.SelectedIndex = 0;

        void Check(bool passed, string message)
        {
            if (!passed) throw new InvalidOperationException(message);
            report("PASS: " + message);
        }
    }
}
