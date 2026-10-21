using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using ProcessSentinel.Core;
using Activity = ProcessSentinel.Core.Activity;
using ActivityKind = ProcessSentinel.Core.ActivityKind;

namespace ProcessSentinel.App;

public sealed class MonitorClient : IAsyncDisposable
{
    public MonitorClient(string? logDirectory = null) => JournalDirectory = SessionLogs.ResolveDirectory(logDirectory);

    private readonly CancellationTokenSource lifetime = new();
    private NamedPipeServerStream? pipe;
    private StreamWriter? commands;
    private Task? receive;
    private EvidenceJournal? journal;
    private EvidenceJournal? reviewJournal;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<Activity> pending = new();
    private readonly ConcurrentQueue<Activity> pendingReview = new();
    private int pendingCount;
    private readonly long[] kinds = new long[6];
    private long total, alerts, sent, received, displaySkipped, etwLost, queueLost;
    private int active;
    private IReadOnlyList<TrackedProcess> processes = Array.Empty<TrackedProcess>();
    private volatile bool running;
    public string JournalPath { get; private set; } = "";
    public string ReviewJournalPath { get; private set; } = "";
    public string JournalDirectory { get; }
    public string Error { get; private set; } = "";
    public bool Running => running;
    public long Total => Interlocked.Read(ref total);
    public long Alerts => Interlocked.Read(ref alerts);
    public long Sent => Interlocked.Read(ref sent);
    public long Received => Interlocked.Read(ref received);
    public long DisplaySkipped => Interlocked.Read(ref displaySkipped);
    public long EtwLost => Interlocked.Read(ref etwLost);
    public long QueueLost => Interlocked.Read(ref queueLost);
    public int ActiveProcesses => Volatile.Read(ref active);
    public IReadOnlyList<TrackedProcess> Processes => Volatile.Read(ref processes);
    public long Count(ActivityKind kind) => Interlocked.Read(ref kinds[(int)kind]);

    public async Task StartAsync(MonitorRequest request)
    {
        var collectorPath = Path.Combine(AppContext.BaseDirectory, "ProcessSentinel.Collector.exe");
        if (!File.Exists(collectorPath)) throw new FileNotFoundException("缺少采集器，请保留发布目录中的所有文件。", collectorPath);
        var id = Guid.NewGuid().ToString("N");
        CreateJournal(request, id);
        pipe = LocalPipe.CreateServer("ProcessSentinel-" + id);
        // UAC is limited to the collector. Targets launched by the UI retain the UI's ordinary token.
        using var collector = Process.Start(new ProcessStartInfo(collectorPath, $"--pipe {id} --parent {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })
            ?? throw new InvalidOperationException("无法启动管理员采集器。");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        await pipe.WaitForConnectionAsync(startup.Token);
        LocalPipe.VerifyClient(pipe, collector.Id);
        commands = new(pipe, leaveOpen: true) { AutoFlush = true };
        await commands.WriteLineAsync(Protocol.Serialize(request));
        running = true;
        receive = Task.Run(ReceiveAsync);
        await ready.Task.WaitAsync(startup.Token);
    }

    private void CreateJournal(MonitorRequest request, string id)
    {
        string path = Path.Combine(JournalDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{request.Root.Id}-{id[..8]}.jsonl");
        string reviewPath = Path.ChangeExtension(path, ".review.jsonl");
        var full = new EvidenceJournal(path, request);
        try { reviewJournal = new(reviewPath, request, reviewOnly: true); }
        catch { full.Dispose(); throw; }
        journal = full;
        JournalPath = path;
        ReviewJournalPath = reviewPath;
    }

    private async Task ReceiveAsync()
    {
        try
        {
            using var reader = new StreamReader(pipe!, leaveOpen: true);
            var lastFlush = Stopwatch.StartNew();
            bool completed = false;
            while (await reader.ReadLineAsync(lifetime.Token) is { } line)
            {
                var message = Protocol.Deserialize<WireMessage>(line);
                if (message is null) continue;
                journal!.Append(message);
                reviewJournal!.Append(message);
                if (message.Type == "error") throw new InvalidOperationException(message.Text);
                if (message.Type == "completed") completed = true;
                if (message.Processes is { } snapshot) Volatile.Write(ref processes, snapshot);
                if (message.Type is "ready" or "statistics" or "completed")
                {
                    Interlocked.Exchange(ref etwLost, message.EtwLost);
                    Interlocked.Exchange(ref queueLost, message.QueueLost);
                    Volatile.Write(ref active, message.ActiveProcesses);
                }
                if (message.Event is { } item)
                {
                    RecordActivity(item);
                }
                if (message.Type == "ready") ready.TrySetResult();
                if (lastFlush.ElapsedMilliseconds >= 1000) { FlushJournals(); lastFlush.Restart(); }
            }
            if (!ready.Task.IsCompleted) ready.TrySetException(new IOException("采集器提前退出。"));
            else if (!completed) Error = "采集器意外断开，日志可能不完整。";
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception ex)
        {
            Error = ex.Message;
            ready.TrySetException(ex);
        }
        finally
        {
            running = false;
            try { FlushJournals(); } catch (Exception ex) { Error = "日志保存失败：" + ex.Message; }
            pipe?.Dispose(); // EOF/errors also terminate the privileged collector via its pipe watcher.
        }
    }
    private void RecordActivity(Activity item)
    {
        Interlocked.Increment(ref total);
        Interlocked.Increment(ref kinds[(int)item.Kind]);
        if (item.Risk != RiskLevel.None)
        {
            Interlocked.Increment(ref alerts);
            // Review events bypass the bounded ordinary display queue.
            pendingReview.Enqueue(item);
        }
        if (item.Kind == ActivityKind.Network && item.Operation == "发送") Interlocked.Add(ref sent, item.Bytes);
        if (item.Kind == ActivityKind.Network && item.Operation == "接收") Interlocked.Add(ref received, item.Bytes);
        if (Interlocked.Increment(ref pendingCount) <= 20000) pending.Enqueue(item);
        else { Interlocked.Decrement(ref pendingCount); Interlocked.Increment(ref displaySkipped); }
    }
    private void FlushJournals()
    {
        try { journal?.Flush(); }
        finally { reviewJournal?.Flush(); }
    }
    public bool TryTakeReview(out Activity? value) => pendingReview.TryDequeue(out value);
    public bool TryTake(out Activity? value)
    {
        if (!pending.TryDequeue(out value)) return false;
        Interlocked.Decrement(ref pendingCount);
        return true;
    }
    public async Task StopAsync()
    {
        if (receive is null) lifetime.Cancel();
        if (commands is not null && running)
        {
            try { await commands.WriteLineAsync("stop"); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        if (receive is not null)
        {
            try { await receive.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException) { Error = "采集器停止超时，日志可能不完整。"; lifetime.Cancel(); pipe?.Dispose(); await receive; }
        }
        running = false;
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lifetime.Cancel();
        try { commands?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        pipe?.Dispose();
        try { journal?.Dispose(); }
        finally { try { reviewJournal?.Dispose(); } finally { lifetime.Dispose(); } }
    }
    public void CopyJournal(string destination) => journal?.CopyTo(destination);
    public void CopyReviewJournal(string destination) => (reviewJournal ?? throw new InvalidOperationException("尚未创建复核日志。")).CopyTo(destination);
    public void CancelStartup() => lifetime.Cancel();
}
