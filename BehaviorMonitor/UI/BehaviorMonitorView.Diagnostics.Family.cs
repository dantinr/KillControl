using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ProcessSentinel.Core;
using ActivityKind = ProcessSentinel.Core.ActivityKind;

namespace ProcessSentinel.App;

public partial class BehaviorMonitorView
{
    internal async Task VerifyFamilyUiAsync(string fixturePath, Action<string> passed)
    {
        void Check(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
            passed("PASS: " + message);
        }
        preview = false;
        timer.Start();
        string directory = Path.Combine(Path.GetTempPath(), "ProcessSentinel-family-" + Guid.NewGuid().ToString("N"));
        using var program = SuspendedProgram.Create(fixturePath, "--family-fixture \"" + directory + "\" root");
        using var target = Process.GetProcessById(program.Id);
        async Task WaitForFiles(string suffix)
        {
            var deadline = Stopwatch.StartNew();
            while (!new[] { "root", "parent", "sibling", "selected" }.All(x => File.Exists(Path.Combine(directory, x + suffix))))
            {
                if (target.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(25)) throw new IOException("Family fixture did not publish its " + suffix + " markers.");
                await Task.Delay(50);
            }
        }
        try
        {
            program.Resume();
            await WaitForFiles(".pid");
            var members = new Dictionary<string, ProcessInfo>();
            foreach (string role in new[] { "root", "parent", "sibling", "selected" })
                members[role] = ProcessCatalog.Get(int.Parse(File.ReadAllText(Path.Combine(directory, role + ".pid"))));
            bool started = await BeginAsync(members["selected"], null);
            Check(started, "selecting a live grandchild starts monitoring its application family: " + StatusText.Text);
            var session = selectedSession!;
            Check(session.Root.Id == program.Id && session.Request.SelectedProcess == members["selected"] && sessions.Count == 1,
                "effective session root is the grandparent application while original selection is retained");
            Check(members.Values.All(x => session.Client.Processes.Any(p => p.IsRunning && p.Process.Id == x.Id)),
                "initial collector roster includes selected worker, parent, application root and sibling");
            Check(!session.Client.Processes.Any(x => x.Process.Id == Environment.ProcessId), "monitoring stops at the ProcessSentinel launcher boundary");
            MonitorTabs.SelectedIndex = 1;
            Tick();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(monitoredProcesses.Single(x => x.Process.Id == members["selected"].Id).RoleText == "所选"
                && monitoredProcesses.Single(x => x.Process.Id == members["parent"].Id).RoleText == "父进程"
                && monitoredProcesses.Single(x => x.Process.Id == program.Id).RoleText == "主进程"
                && TargetScope.Text.Contains(members["selected"].Label) && TargetTitle.Text.Contains(program.Id.ToString()),
                "bound UI distinguishes original selection, parent and root and displays the expanded scope");
            Check(await BeginAsync(members["sibling"], null) && sessions.Count == 1 && selectedSession == session,
                "selecting another member of the same program reuses its existing collector and journal");
            File.WriteAllText(Path.Combine(directory, "run"), "run");
            await WaitForFiles(".done");
            await Task.Delay(1500);
            await StopAsync();
            while (session.Client.TryTake(out var item)) session.Retain(item!);
            var events = EvidenceJournal.ReadEvents(session.Client.JournalPath).ToArray();
            foreach (var member in members)
                Check(events.Any(x => x.ProcessId == member.Value.Id && x.Kind == ActivityKind.File && x.Operation == "写入"
                        && x.Target.Contains(Path.Combine(directory, member.Key))), "real file writes are attributed to " + member.Key + " PID " + member.Value.Id);
            Check(events.Any(x => x.Kind == ActivityKind.Network && x.Bytes > 0)
                && events.Any(x => x.Kind == ActivityKind.Registry && x.Operation == "设置值")
                && events.Any(x => x.Kind == ActivityKind.Process && x.Operation == "启动" && !members.Values.Any(p => p.Id == x.ProcessId)),
                "family scope captures network, registry and newly created descendants after startup");
            Check(events.Length == session.Client.Total && session.Client.EtwLost == 0 && session.Client.QueueLost == 0 && string.IsNullOrEmpty(session.Client.Error),
                "family journal drains completely with zero reported ETW or queue loss");
            string snapshotPath = Path.Combine(directory, "journal-copy.jsonl");
            session.Client.CopyJournal(snapshotPath);
            var messages = File.ReadLines(snapshotPath).Select(Protocol.Deserialize<WireMessage>).Where(x => x is not null).ToArray();
            var finalRoster = messages.Last(x => x!.Type == "completed")!.Processes!;
            Check(finalRoster.Single(x => x.IsSelected).Process.Id == members["selected"].Id
                && finalRoster.Any(x => x.Process.Id == members["parent"].Id && x.IsAncestor)
                && finalRoster.Single(x => x.IsRoot).Process.Id == program.Id,
                "journal snapshots preserve original selection, parent ancestry and effective root");
            Check(events.Any(x => x.Kind == ActivityKind.System && x.Detail.Contains("所选") && x.Detail.Contains("追溯边界")),
                "persisted start event explains the selected process and application boundary");
            Check(!target.HasExited && members.Values.All(x => ProcessCatalog.ReadDetails(x).StartTimeUtcTicks == x.StartTimeUtcTicks),
                "stopping monitoring leaves all original family processes running with unchanged identities");
            Check(!EtwMonitor.IsAdministrator(), "family WPF test ran with ordinary permissions");
            passed($"Captured {session.Client.Total} events. Evidence: {session.Client.JournalPath}");
        }
        finally
        {
            timer.Stop();
            await StopAllAsync();
            if (Directory.Exists(directory)) File.WriteAllText(Path.Combine(directory, "exit"), "exit");
            if (!target.HasExited)
            {
                try { await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException) { target.Kill(entireProcessTree: true); await target.WaitForExitAsync(); }
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
