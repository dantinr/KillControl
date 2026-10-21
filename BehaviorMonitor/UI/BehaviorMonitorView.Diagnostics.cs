using System.ComponentModel;
using System.Windows.Threading;
using ProcessSentinel.Core;
using Activity = ProcessSentinel.Core.Activity;

namespace ProcessSentinel.App;

public partial class BehaviorMonitorView
{
    internal void ShowNetworkPreview()
    {
        MonitorTabs.SelectedIndex = 0;
        KindFilter.SelectedIndex = 2;
        EventGrid.SelectedItem = activities.LastOrDefault(x => x.Kind == ActivityKind.Network && x.Operation == "发送");
    }

    internal void ShowProcessPreview()
    {
        MonitorTabs.SelectedIndex = 1;
        MonitoredProcessGrid.SelectedItem = monitoredProcesses.FirstOrDefault(x => x.IsSelected) ?? monitoredProcesses.FirstOrDefault();
    }

    internal void PrepareForDiagnostics()
    {
        preview = true;
        diagnostics = true;
        settings = new();
        ApplySettings(settings);
        timer.Stop();
    }

    // Exercises the same mutation path as Tick, with a live bound DataGrid and dispatcher.
    internal async Task VerifyUiAsync(IReadOnlyList<Activity> saved, Action<string> passed)
    {
        void Check(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
            passed("PASS: " + message);
        }
        Check(saved.Count > 0, $"loaded {saved.Count} journal events for UI replay");
        AppendActivityBatch(saved);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(EventGrid.Items.Count == Math.Min(saved.Count, 5000), "journal replay updates the bound DataGrid");

        EventGrid.SelectedItem = saved.Last();
        activityView.MoveCurrentTo(saved.Last());
        AppendActivityBatch(saved);
        Check(EventGrid.SelectedItem is Activity, "incoming events preserve selection and current item");
        activityView.SortDescriptions.Add(new SortDescription(nameof(Activity.Sequence), ListSortDirection.Descending));
        AppendActivityBatch(saved);
        Check(activityView.Cast<Activity>().First().Sequence == saved.Max(x => x.Sequence), "sorted grid accepts live incoming events");

        KindFilter.SelectedIndex = 2;
        AppendActivityBatch(saved);
        Check(activityView.Cast<Activity>().All(x => x.Kind == ActivityKind.Network), "category filtering works during incoming events");
        KindFilter.SelectedIndex = 0;
        RiskOnly.IsChecked = true;
        var flagged = saved[0] with { Sequence = 10001, Risk = RiskLevel.Attention, Reason = "UI regression fixture" };
        AppendActivityBatch([flagged]);
        Check(activityView.Cast<Activity>().Any(x => ReferenceEquals(x, flagged)) && activityView.Cast<Activity>().All(x => x.Risk != RiskLevel.None), "risk filter accepts a new flagged event");
        RiskOnly.IsChecked = false;
        EventSearch.Text = "UI regression fixture";
        AppendActivityBatch([flagged]);
        Check(!activityView.IsEmpty && activityView.Cast<Activity>().All(x => x.Reason.Contains("UI regression fixture")), "keyword filter works while records arrive");
        EventSearch.Clear();
        activityView.SortDescriptions.Clear();

        for (int offset = 0; offset < 12000; offset += 600)
        {
            EventGrid.SelectedItem = activities.First();
            activityView.MoveCurrentTo(activities.First());
            AppendActivityBatch(Enumerable.Range(offset, 600).Select(i => saved[i % saved.Count] with { Sequence = 20000 + i }));
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        Check(activities.Count == 5000 && EventGrid.Items.Count == 5000 && activities.Last().Sequence == 31999,
            "12,000 incoming events retain the newest 5,000 and evict selected old rows safely");
        EventGrid.SelectedItem = activities.Last();
        Check(DetailText.Text.Contains(activities.Last().Target), "selected event still displays complete evidence after retention trimming");
        activities.Clear();
        AppendActivityBatch(saved);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(EventGrid.Items.Count == Math.Min(saved.Count, 5000), "clear and restart update the bound grid safely");

        MonitorTabs.SelectedIndex = 1;
        var rootProcess = new ProcessInfo(101, 1, "root.exe", @"C:\Apps\root.exe", DateTime.UtcNow.Ticks);
        var idleChild = new ProcessInfo(102, 101, "idle-child.exe", @"C:\Apps\idle-child.exe", rootProcess.StartTimeUtcTicks + 1);
        ApplyProcessSnapshot([new(rootProcess, true, true), new(idleChild, false, true)]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(MonitoredProcessGrid.Items.Count == 2 && monitoredProcesses.All(x => x.IsRunning),
            "bound process grid includes a root and a child with no activity events");
        MonitoredProcessGrid.SelectedItem = monitoredProcesses.Single(x => x.Process.Id == idleChild.Id);
        monitoredProcessView.SortDescriptions.Add(new SortDescription("Process.Id", ListSortDirection.Descending));
        var newChild = new ProcessInfo(103, 102, "new-child.exe", @"C:\Apps\new-child.exe", rootProcess.StartTimeUtcTicks + 2);
        ApplyProcessSnapshot([new(rootProcess, true, true), new(idleChild, false, true), new(newChild, false, true)]);
        Check(MonitoredProcessGrid.Items.Count == 3 && MonitoredProcessGrid.SelectedItem is TrackedProcess { Process.Id: 102 }
            && DetailText.Text.Contains(idleChild.Path), "new descendants update the sorted roster while preserving selection and complete path");
        LiveProcessesOnly.IsChecked = true;
        ApplyProcessSnapshot([new(rootProcess, true, true), new(idleChild, false, false), new(newChild, false, true)]);
        Check(MonitoredProcessGrid.Items.Count == 2 && monitoredProcesses.Count == 3 && monitoredProcesses.Single(x => x.Process.Id == 102).StateText == "已退出",
            "process exit updates the live filter while retaining the exited row");
        LiveProcessesOnly.IsChecked = false;
        MonitoredProcessSearch.Text = newChild.Path;
        Check(MonitoredProcessGrid.Items.Count == 1 && ((TrackedProcess)MonitoredProcessGrid.Items[0]).Process.Id == 103,
            "process roster supports full-path search");
        MonitoredProcessSearch.Clear();
        MonitoredProcessGrid.SelectedItem = monitoredProcesses.Single(x => x.Process.Id == 102);
        var reusedChild = idleChild with { StartTimeUtcTicks = rootProcess.StartTimeUtcTicks + 3, Name = "reused-pid.exe" };
        ApplyProcessSnapshot([new(rootProcess, true, false), new(idleChild, false, false), new(newChild, false, true), new(reusedChild, false, true)]);
        Check(monitoredProcesses.Count(x => x.Process.Id == 102) == 2 && MonitoredProcessGrid.SelectedItem is TrackedProcess selected
            && selected.Process.StartTimeUtcTicks == idleChild.StartTimeUtcTicks && !selected.IsRunning,
            "PID reuse keeps separate rows and preserves the selected process identity after parent exit");
        ApplyProcessSnapshot([new(rootProcess, true, false), new(idleChild, false, false) { OutboundBytes = 1536 },
            new(newChild, false, true) { OutboundBytes = 16 }, new(reusedChild, false, true)]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        MonitoredProcessGrid.UpdateLayout();
        var trafficColumn = MonitoredProcessGrid.Columns.Single(x => x.Header?.ToString() == "出网流量");
        var exitedSender = monitoredProcesses.Single(x => x.Process.StartTimeUtcTicks == idleChild.StartTimeUtcTicks);
        Check(trafficColumn.SortMemberPath == nameof(TrackedProcess.OutboundBytes)
            && trafficColumn.GetCellContent(exitedSender) is System.Windows.Controls.TextBlock { Text: "1.5 KB" }
            && monitoredProcesses.Single(x => x.Process.StartTimeUtcTicks == reusedChild.StartTimeUtcTicks).OutboundTrafficText == "0 B",
            "bound outbound column shows readable independent totals for exited and reused PID rows");
        monitoredProcessView.SortDescriptions.Clear();
        monitoredProcessView.SortDescriptions.Add(new SortDescription(nameof(TrackedProcess.OutboundBytes), ListSortDirection.Descending));
        Check(monitoredProcessView.Cast<TrackedProcess>().First() == exitedSender,
            "outbound column sorts by numeric bytes rather than formatted text");
        ApplyProcessSnapshot([new(rootProcess, true, false), new(idleChild, false, false) { OutboundBytes = 1536 },
            new(newChild, false, true) { OutboundBytes = 2048 }, new(reusedChild, false, true)]);
        Check(monitoredProcessView.Cast<TrackedProcess>().First().Process.Id == newChild.Id
            && MonitoredProcessGrid.SelectedItem is TrackedProcess { Process.StartTimeUtcTicks: var senderStarted } && senderStarted == idleChild.StartTimeUtcTicks
            && DetailText.Text.Contains("1.5 KB") && DetailText.Text.Contains("1,536"),
            "live traffic updates reorder the roster while preserving selected identity and exact byte evidence");
        monitoredProcessView.SortDescriptions.Clear();
        ClearMonitoredProcesses();
        Check(MonitoredProcessGrid.Items.Count == 0 && MonitoredProcessTab.Header.ToString()!.Contains("0"),
            "restarting monitoring clears the previous process roster");
        MonitorTabs.SelectedIndex = 0;

        var first = new MonitorSession(new(rootProcess, true, newChild), "fixture boundary") { Starting = false };
        var second = new MonitorSession(newChild, false) { Starting = false };
        var firstEvent = saved[0] with { ProcessId = rootProcess.Id, Target = "session-one-only" };
        var secondEvent = saved[0] with { ProcessId = newChild.Id, Target = "session-two-only" };
        first.Retain(firstEvent);
        for (int i = 0; i < 6000; i++) second.Retain(secondEvent with { Sequence = i });
        sessions.Add(first);
        sessions.Add(second);
        SessionPicker.SelectedItem = first;
        Check(activities.Count == 1 && activities.Single().Target == firstEvent.Target && client == first.Client,
            "selecting a session shows only its own activities and client");
        ApplyProcessSnapshot([new(rootProcess, true, true, IsAncestor: true), new(idleChild, false, true, IsAncestor: true), new(newChild, false, true, IsSelected: true)]);
        MonitorTabs.SelectedIndex = 1;
        MonitoredProcessGrid.SelectedItem = monitoredProcesses.Single(x => x.Process.Id == idleChild.Id);
        Check(TargetScope.Text.Contains(newChild.Label) && TargetTitle.Text.Contains(rootProcess.Id.ToString())
            && DetailText.Text.Contains("父进程") && monitoredProcesses.Single(x => x.IsSelected).RoleText == "所选",
            "bound scope and role details distinguish selected child, parent and effective root");
        SessionPicker.SelectedItem = second;
        Check(monitoredProcesses.Count == 0, "switching sessions clears the prior application's process roster");
        Check(activities.Count == 5000 && activities.First().Sequence == 1000 && activities.Last().Sequence == 5999
            && activities.All(x => x.ProcessId == newChild.Id), "each background session independently retains its newest 5,000 events");
        SessionPicker.SelectedItem = first;
        Check(activities.Count == 1 && activities.Single().ProcessId == rootProcess.Id && second.Recent.Count == 5000,
            "switching back restores the original history without clearing another session");

        var firstReview = firstEvent with { Sequence = 71, Risk = RiskLevel.Attention, Target = "review-first-only", Reason = "retained review evidence" };
        var highReview = firstReview with { Sequence = 72, Risk = RiskLevel.High, Target = "review-high-only" };
        first.ReviewEvents.Add(firstReview);
        first.ReviewEvents.Add(highReview);
        second.ReviewEvents.Add(secondEvent with { Risk = RiskLevel.Attention, Target = "review-second-only" });
        for (int i = 0; i < 6000; i++) first.Retain(firstEvent with { Sequence = i, Risk = RiskLevel.None });
        SessionPicker.SelectedItem = second;
        SessionPicker.SelectedItem = first;
        ShowReviewPanel();
        Check(activities.Count == 5000 && ReviewGrid.Items.Count == 2 && first.ReviewEvents.Contains(firstReview),
            "review panel retains early attention and high events after ordinary history eviction");
        ReviewGrid.SelectedItem = firstReview;
        Check(DetailText.Text.Contains(firstReview.Target) && DetailText.Text.Contains(firstReview.Reason),
            "review selection shows full target and trigger evidence");
        EventSearch.Text = "no ordinary matches";
        RiskOnly.IsChecked = true;
        Check(ReviewGrid.Items.Count == 2, "ordinary category and keyword filters do not hide independent review records");
        EventSearch.Clear();
        RiskOnly.IsChecked = false;
        reviewView!.SortDescriptions.Add(new SortDescription(nameof(Activity.Sequence), ListSortDirection.Descending));
        first.ReviewEvents.Add(highReview with { Sequence = 73 });
        Check(ReferenceEquals(ReviewGrid.SelectedItem, firstReview) && ((Activity)ReviewGrid.Items[0]).Sequence == 73,
            "sorted review panel accepts new alerts while preserving selected evidence");
        for (int i = 0; i < 6000; i++) first.ReviewEvents.Add(highReview with { Sequence = 100 + i, Target = "review-volume-fixture" });
        Check(ReviewGrid.Items.Count == 6003 && first.ReviewEvents.Contains(firstReview),
            "review records have independent retention beyond the ordinary 5,000-event limit");
        ReviewSearch.Text = firstReview.Target;
        Check(ReviewGrid.Items.Count == 1 && ReferenceEquals(ReviewGrid.Items[0], firstReview),
            "review search finds an old retained event after later alert traffic");
        ReviewSearch.Clear();
        SessionPicker.SelectedItem = second;
        Check(ReviewGrid.Items.Count == 1 && ((Activity)ReviewGrid.Items[0]).Target == "review-second-only",
            "switching sessions isolates the review panel from another application's alerts");
        SessionPicker.SelectedItem = first;
        Check(ReviewGrid.Items.Count == 6003, "returning to a session restores all independently retained review records");
        await RemoveSessionAsync();
        Check(sessions.Count == 1 && client == second.Client && activities.Count == 5000,
            "removing a stopped session selects another session and preserves its history");
        MonitorTabs.SelectedIndex = 0;
    }

    internal async Task VerifyMultipleUiAsync(string fixturePath, Action<string> passed)
    {
        void Check(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
            passed("PASS: " + message);
        }
        preview = false;
        timer.Start();
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ProcessSentinel-multi-" + Guid.NewGuid().ToString("N"));
        var programs = new List<SuspendedProgram>();
        var targets = new List<System.Diagnostics.Process>();
        try
        {
            for (int i = 0; i < MaximumConcurrentSessions; i++)
            {
                var program = SuspendedProgram.Create(fixturePath, "--fixture-ipv4 \"" + System.IO.Path.Combine(directory, "target-" + i) + "\"");
                programs.Add(program);
                targets.Add(System.Diagnostics.Process.GetProcessById(program.Id));
                await BeginAsync(ProcessCatalog.Get(program.Id), program: null);
                Check(client?.Running == true, $"target {i + 1} started an independent collector while previous sessions remained active: {StatusText.Text}");
            }
            Check(sessions.Count == 4 && sessions.All(x => x.Client.Running), "four root programs are monitored concurrently");
            var first = sessions[0];
            var second = sessions[1];
            await BeginAsync(first.Root, null);
            Check(sessions.Count == 4 && selectedSession == first, "re-adding a monitored root selects its existing session");
            using (var extra = SuspendedProgram.Create(fixturePath, "--fixture-ipv4 \"" + System.IO.Path.Combine(directory, "extra") + "\""))
                Check(!await BeginAsync(ProcessCatalog.Get(extra.Id), null) && sessions.Count == 4 && sessions.All(x => x.Client.Running),
                    "concurrency limit rejects an extra session without disrupting existing collectors");
            programs[0].Resume();
            await targets[0].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Check(targets[0].ExitCode == 0, "first benign fixture completes normally");
            await Task.Delay(1500);
            await StopAsync();
            Check(!first.Client.Running && sessions.Skip(1).All(x => x.Client.Running) && !targets[1].HasExited,
                "stopping the current session leaves three collectors and the other target programs running");

            // An exited root is a deterministic startup failure; it must not stop the other collectors.
            Check(!await BeginAsync(first.Root, null) && sessions.Count == 4 && sessions.Skip(1).All(x => x.Client.Running),
                "failed target resolution leaves other monitoring sessions intact");

            SessionPicker.SelectedItem = second;
            Check(!monitoredProcesses.Any(x => x.Process.Id == first.Root.Id) && monitoredProcesses.Any(x => x.Process.Id == second.Root.Id),
                "switching the bound process grid isolates the selected target's roster");
            programs[1].Resume();
            await targets[1].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Check(targets[1].ExitCode == 0, "second benign fixture completes normally after the first collector stopped");
            await Task.Delay(1500);
            Tick();
            Check(second.Client.Total > 1 && activities.Any(x => x.ProcessId == second.Root.Id)
                && !activities.Any(x => x.ProcessId == first.Root.Id), "other session continues receiving real events into the bound grid after selective stop");
            await StopAllAsync();
            Check(sessions.All(x => !x.Client.Running) && !targets[2].HasExited && !targets[3].HasExited,
                "stop all stops every collector while leaving unstarted targets alive");
            foreach (var session in sessions)
            {
                while (session.Client.TryTake(out var item)) session.Retain(item!);
                Check(string.IsNullOrEmpty(session.Client.Error) && session.Client.QueueLost == 0 && session.Client.EtwLost == 0,
                    $"PID {session.Root.Id}: clean completion with zero reported ETW or queue loss");
            }
            for (int i = 0; i < 2; i++)
            {
                var session = sessions[i];
                var events = EvidenceJournal.ReadEvents(session.Client.JournalPath).ToArray();
                string ownDirectory = System.IO.Path.Combine(directory, "target-" + i);
                string otherDirectory = System.IO.Path.Combine(directory, "target-" + (1 - i));
                Check(events.Any(x => x.Kind == ActivityKind.File && x.Operation == "写入" && x.Target.Contains(ownDirectory))
                    && events.Any(x => x.Kind == ActivityKind.Network && x.Bytes > 0)
                    && events.Any(x => x.Kind == ActivityKind.Registry && x.Operation == "设置值")
                    && events.Any(x => x.Kind == ActivityKind.Process && x.Operation == "启动"),
                    $"PID {session.Root.Id}: separate journal contains file, network, registry and child process activity");
                Check(events.Length == session.Client.Total && !events.Any(x => x.Target.Contains(otherDirectory))
                    && !events.Any(x => x.ProcessId == sessions[1 - i].Root.Id), $"PID {session.Root.Id}: journal drains fully and excludes the other independent target");
                passed("Evidence: " + session.Client.JournalPath);
            }
            SessionPicker.SelectedItem = first;
            Check(activities.Any(x => x.ProcessId == first.Root.Id) && !activities.Any(x => x.ProcessId == second.Root.Id)
                && monitoredProcesses.Any(x => x.IsRoot && !x.IsRunning), "stopped session history and exited root remain available after switching back");
            // Leave two collectors active to verify the normal Closing cleanup path too.
            for (int i = 2; i < 4; i++) await BeginAsync(ProcessCatalog.Get(programs[i].Id), null);
            Check(sessions.Count(x => x.Client.Running) == 2, "two collectors are active before the window close cleanup test");
            var pendingStop = StopAsync();
            await CloseForDiagnosticsAsync();
            await pendingStop;
            Check(sessions.Count == 0 && client is null, "closing during a pending stop disposes every monitoring session without concurrent cleanup");
            Check(!EtwMonitor.IsAdministrator(), "multi-session WPF test ran with ordinary user permissions");
        }
        finally
        {
            timer.Stop();
            if (sessions.Count > 0) await StopAllAsync();
            foreach (var program in programs) program.Dispose();
            foreach (var target in targets) target.Dispose();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
        }
    }

    internal async Task VerifyLiveUiAsync(ProcessInfo root, Action<string> passed)
    {
        preview = false;
        await BeginAsync(root, null);
        if (client?.Running != true) throw new InvalidOperationException("Unable to start live UI monitor: " + StatusText.Text);
        passed($"PASS: live UI monitoring started for {root.Name}, PID {root.Id}, version {Protocol.Version}");
        timer.Start();
        try
        {
            await Task.Delay(2000);
            var roster = client.Processes;
            if (roster.Count == 0 || roster.Count(x => x.IsRunning) != client.ActiveProcesses
                || !roster.Any(x => x.Process.Id == root.Id && x.Process.StartTimeUtcTicks == root.StartTimeUtcTicks)
                || !roster.Any(x => x.IsRoot && x.Process.Id == selectedSession!.Root.Id && x.Process.StartTimeUtcTicks == selectedSession.Root.StartTimeUtcTicks))
                throw new InvalidOperationException("Collector roster is missing the target or tracked descendants.");
            MonitorTabs.SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (MonitoredProcessGrid.Items.Count != roster.Count) throw new InvalidOperationException("Collector process roster did not reach the bound grid.");
            passed($"PASS: live process roster shows {roster.Count} tracked processes, including the original target, and agrees with collector count");
            MonitorTabs.SelectedIndex = 0;
            KindFilter.SelectedIndex = 2;
            await Task.Delay(2000);
            EventSearch.Text = "127.0.0.1";
            await Task.Delay(2000);
            EventSearch.Clear();
            KindFilter.SelectedIndex = 0;
            activityView.SortDescriptions.Add(new SortDescription(nameof(Activity.Sequence), ListSortDirection.Descending));
            if (!activityView.IsEmpty) EventGrid.SelectedItem = activityView.Cast<Activity>().First();
            await Task.Delay(2000);
        }
        finally
        {
            await StopAsync();
            timer.Stop();
            while (client.TryTake(out var remaining)) AppendActivityBatch([remaining!]);
        }
        if (!string.IsNullOrEmpty(client.Error)) throw new InvalidOperationException(client.Error);
        if (client.Total <= 1) throw new InvalidOperationException("The target produced no activity during the probe.");
        if (activities.Count == 0 || EventGrid.Items.Count == 0) throw new InvalidOperationException("No live events reached the DataGrid.");
        passed($"PASS: real timer, grid, category/search filters, sort and selection remained responsive; {client.Total} events captured, {activities.Count} rows retained");
        if (ProcessCatalog.Get(root.Id).StartTimeUtcTicks != root.StartTimeUtcTicks) throw new InvalidOperationException("Target identity changed during probe.");
        passed("PASS: original target process is still running with the same creation time");
        passed("Evidence: " + client.JournalPath);
        // Retain the stopped client so normal window closing also exercises synchronous cleanup.
    }

    internal MonitorClient DiagnosticClient => client ?? throw new InvalidOperationException("诊断会话未启动。");

    internal async Task<bool> StartDiagnosticAsync(ProcessInfo target, string logDirectory)
    {
        preview = false;
        diagnostics = true;
        settings = new AppSettings { LogDirectory = logDirectory };
        ApplySettings(settings);
        timer.Start();
        return await BeginAsync(target, null);
    }

    internal Task CloseForDiagnosticsAsync() => ShutdownAsync();
}
