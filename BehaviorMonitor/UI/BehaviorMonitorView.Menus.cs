using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProcessSentinel.Core;

namespace ProcessSentinel.App;

public partial class BehaviorMonitorView
{
    private AppSettings settings = new();

    private bool CanAddMonitor(ProcessInfo process) => !starting && !stopping && !closing
        && process.Id > 0 && process.StartTimeUtcTicks > 0 && process.Id != Environment.ProcessId
        && (sessions.Count(x => x.Client.Running) < MaximumConcurrentSessions
            || FindRunningSession(process, IncludeChildren.IsChecked == true) is not null);

    private void ProcessItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (starting || stopping || closing || sender is not ListBoxItem item) return;
        item.IsSelected = true;
        item.Focus();
    }

    private void ProcessContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.Items[0] is not MenuItem entry) return;
        var process = (menu.PlacementTarget as ListBoxItem)?.Content as ProcessInfo;
        entry.IsEnabled = process is not null && CanAddMonitor(process);
        entry.ToolTip = process?.Label;
    }

    private async void AddProcessMonitor_Click(object sender, RoutedEventArgs e)
    {
        // Context menus have a separate visual tree; use their actual owning row.
        if (sender is not MenuItem entry || ItemsControl.ItemsControlFromItemContainer(entry) is not ContextMenu menu
            || menu.PlacementTarget is not ListBoxItem { Content: ProcessInfo process } || !CanAddMonitor(process)) return;
        ProcessList.SelectedItem = process;
        await BeginAsync(process, null);
    }

    private void ApplySettings(AppSettings value)
    {
        IncludeChildren.IsChecked = value.IncludeProgramTree;
        RiskOnly.IsChecked = value.RiskOnly;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (starting || stopping || closing) return;
        var dialog = new SettingsWindow(settings) { Owner = HostWindow };
        if (dialog.ShowDialog() != true) return;
        settings = dialog.Settings;
        ApplySettings(settings);
        StatusText.Text = "设置已保存 · 新会话日志目录：" + settings.EffectiveLogDirectory + " · 风险筛选已更新";
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = HostWindow }.ShowDialog();
}
