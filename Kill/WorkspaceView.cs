using System.Windows;
using System.Windows.Controls;

namespace Kill;

public class WorkspaceView : UserControl
{
    private TaskCompletionSource? _pendingOperation;
    internal Task WhenIdle => _pendingOperation?.Task ?? Task.CompletedTask;

    protected void SetPendingOperation(bool busy)
    {
        if (busy) _pendingOperation ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        else { _pendingOperation?.TrySetResult(); _pendingOperation = null; }
    }

    protected Window HostWindow => Window.GetWindow(this)
        ?? throw new InvalidOperationException("页面尚未连接到主窗口。");
}
