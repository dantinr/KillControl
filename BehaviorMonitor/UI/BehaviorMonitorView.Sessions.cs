using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ProcessSentinel.Core;
using Activity = ProcessSentinel.Core.Activity;

namespace ProcessSentinel.App;

public partial class BehaviorMonitorView
{
    private const int MaximumConcurrentSessions = 4;
    private readonly ObservableCollection<MonitorSession> sessions = new();
    private MonitorSession? selectedSession, startupSession;
    private bool stopping;
    private bool cancelStartupRequested;
    private TaskCompletionSource? startupFinished;

    private void ScopeOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SetControls(client?.Running == true);
    }

    private MonitorSession? FindRunningSession(ProcessInfo process, bool matchFamily = false) => sessions.FirstOrDefault(x => (x.Starting || x.Client.Running)
        && ((x.Root.Id == process.Id && x.Root.StartTimeUtcTicks == process.StartTimeUtcTicks)
            || (matchFamily && x.IncludeChildren && x.Client.Processes.Any(p => p.IsRunning
                && p.Process.Id == process.Id && p.Process.StartTimeUtcTicks == process.StartTimeUtcTicks))));

    private void SessionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedSession = SessionPicker.SelectedItem as MonitorSession;
        client = selectedSession?.Client;
        activities.Clear();
        BindReviewSession();
        ClearMonitoredProcesses();
        ResetCounters();
        DetailText.Text = "选择当前程序的行为记录或进程，查看完整信息。";
        if (selectedSession is { } session)
        {
            TargetTitle.Text = session.Root.Label;
            TargetPath.Text = session.Root.PathText;
            TargetScope.Text = session.ScopeText;
            TargetScope.ToolTip = session.Boundary;
            AppendActivityBatch(session.Recent);
            ApplyProcessSnapshot(session.Client.Processes);
        }
        else
        {
            TargetTitle.Text = "让程序的行为变得可见";
            TargetPath.Text = "选择进程后添加监控，可同时监控多个程序。";
            TargetScope.Text = "选中子进程时，自动纳入所属程序的相关父进程、兄弟及子孙进程。";
            HistoryHint.Text = "每个监控程序独立保存完整日志。";
            StatusText.Text = "就绪 · 选择进程后添加监控";
        }
        SetControls(client?.Running == true);
        Tick();
    }

    private void DrainSession(MonitorSession session)
    {
        var batch = new List<Activity>(600);
        for (int i = 0; i < 600 && session.Client.TryTake(out var value); i++)
        {
            session.Retain(value!);
            batch.Add(value!);
        }
        for (int i = 0; i < 600 && session.Client.TryTakeReview(out var review); i++)
            session.ReviewEvents.Add(review!);
        if (ReferenceEquals(selectedSession, session)) AppendActivityBatch(batch);
        if (!session.Starting && !session.Client.Running) session.Duration.Stop();
        session.UpdateLabel();
    }

    private void UpdateSessionSummary()
    {
        int running = sessions.Count(x => x.Client.Running && !x.Starting);
        SessionCount.Text = $"监控中 {running} / {MaximumConcurrentSessions} · 会话 {sessions.Count}";
        RunBadge.Text = starting ? $"●  连接中 · 其他 {running} 个程序监控中"
            : running > 0 ? $"●  {running} 个程序监控中" : sessions.Count > 0 ? "●  监控已停止" : "●  等待选择目标";
        StopAllButton.IsEnabled = !stopping && !closing && (starting || running > 0);
        RemoveSessionButton.IsEnabled = !starting && !stopping && !closing && selectedSession is { Starting: false } item && !item.Client.Running;
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e) => await StopAllAsync();

    private async Task StopAllAsync()
    {
        if (stopping) return;
        stopping = true;
        cancelStartupRequested = true;
        startupSession?.Client.CancelStartup();
        SetControls(false);
        try
        {
            if (starting && startupFinished is { } completion) await completion.Task;
            // Each collector receives its own stop command; never stop another application's ETW session.
            await Task.WhenAll(sessions.Select(x => x.Client.StopAsync()));
            Tick();
        }
        finally { stopping = false; SetControls(client?.Running == true); }
    }

    private async void RemoveSession_Click(object sender, RoutedEventArgs e) => await RemoveSessionAsync();

    private async Task RemoveSessionAsync()
    {
        if (starting || stopping || selectedSession is not { } session || session.Client.Running) return;
        stopping = true;
        SetControls(false);
        try
        {
            await session.Client.DisposeAsync();
            sessions.Remove(session);
            SessionPicker.SelectedItem = sessions.LastOrDefault();
        }
        finally { stopping = false; SetControls(client?.Running == true); }
    }
}
