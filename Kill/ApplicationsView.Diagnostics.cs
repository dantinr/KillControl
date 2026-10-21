using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kill.Models;

namespace Kill;

public partial class ApplicationsView
{
    internal async Task VerifyRuntimeUiAsync(Action<string> pass)
    {
        await InitializationTask;
        _runtimeTimer.Stop();
        await _runtimeRefreshTask;
        var application = SelectedApplication ?? throw new InvalidOperationException("没有可验证的应用列表项。");
        var original = application.RuntimeStatus;
        var order = _applications.ToArray();
        try
        {
            ApplicationsList.ScrollIntoView(application);
            ApplicationsList.UpdateLayout();
            var container = (ListBoxItem)ApplicationsList.ItemContainerGenerator.ContainerFromItem(application);
            var label = FindLabel(container) ?? throw new InvalidOperationException("列表运行状态标签未生成。");
            foreach (var state in new[] { ApplicationRunState.Running, ApplicationRunState.NotDetected, ApplicationRunState.Unknown })
            {
                application.RuntimeStatus = new ApplicationRuntimeStatus(state,
                    state == ApplicationRunState.Running ? [new ApplicationProcess(12345, @"C:\Example\app.exe")] : [],
                    "界面回归示例", DateTimeOffset.UtcNow);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (DetailRuntimeStatus.Text != application.RuntimeStatus.Label || label.Text != application.RuntimeStatus.Label)
                    throw new InvalidOperationException("应用列表和详情的运行状态没有同步更新。");
                pass("PASS: application runtime state updates both list and details: " + state);
            }
            if (!ReferenceEquals(SelectedApplication, application) || !order.SequenceEqual(_applications))
                throw new InvalidOperationException("更新运行状态改变了应用排序或选择。");
            pass("PASS: updating runtime state preserves application order and selection");
        }
        finally
        {
            application.RuntimeStatus = original;
            if (IsVisible && !_shuttingDown) _runtimeTimer.Start();
        }

        static TextBlock? FindLabel(DependencyObject parent)
        {
            if (parent is TextBlock { Name: "ListRuntimeStatus" } text) return text;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindLabel(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
            return null;
        }
    }
}
