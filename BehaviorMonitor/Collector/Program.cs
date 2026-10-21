using System.IO.Pipes;
using System.Threading.Channels;
using ProcessSentinel.Core;

if (args.Length != 4 || args[0] != "--pipe" || !Guid.TryParseExact(args[1], "N", out _) || args[2] != "--parent" || !int.TryParse(args[3], out int parentId) || parentId <= 0) return 2;
using var lifetime = new CancellationTokenSource();
using var pipe = new NamedPipeClientStream(".", "ProcessSentinel-" + args[1], PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
try
{
    await pipe.ConnectAsync(20000);
    LocalPipe.VerifyServer(pipe, parentId);
    using var reader = new StreamReader(pipe, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    var configLine = await reader.ReadLineAsync(lifetime.Token);
    var request = configLine is null ? null : Protocol.Deserialize<MonitorRequest>(configLine);
    if (request is null) return 3;
    var queue = Channel.CreateBounded<WireMessage>(new BoundedChannelOptions(10000) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    long queueLost = 0;
    using var monitor = new EtwMonitor();
    monitor.ActivityReceived += item => { if (!queue.Writer.TryWrite(new("event", Event: item))) Interlocked.Increment(ref queueLost); };
    try { monitor.Start(request); }
    catch (Exception ex) { await writer.WriteLineAsync(Protocol.Serialize(new WireMessage("error", Text: ex.Message))); return 4; }
    var initialProcesses = monitor.GetProcessSnapshot()!;
    await writer.WriteLineAsync(Protocol.Serialize(new WireMessage("ready", Text: "采集器就绪",
        ActiveProcesses: initialProcesses.ActiveCount, Processes: initialProcesses.Processes)));
    var send = Task.Run(async () =>
    {
        await foreach (var message in queue.Reader.ReadAllAsync()) await writer.WriteLineAsync(Protocol.Serialize(message));
    });
    var statistics = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long processRevision = initialProcesses.Revision;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var snapshot = monitor.GetProcessSnapshot(processRevision);
                if (queue.Writer.TryWrite(new("statistics", EtwLost: monitor.EventsLost, QueueLost: Interlocked.Read(ref queueLost),
                    ActiveProcesses: snapshot?.ActiveCount ?? monitor.ActiveProcesses, Processes: snapshot?.Processes)) && snapshot is not null)
                    processRevision = snapshot.Revision;
            }
        }
        catch (OperationCanceledException) { }
    });
    var command = Task.Run(async () =>
    {
        while (await reader.ReadLineAsync(lifetime.Token) is { } line) if (line == "stop") return;
    });
    try
    {
        var first = await Task.WhenAny(command, monitor.Completion, send);
        if (first == monitor.Completion) await monitor.Completion; // Surface unexpected trace failures.
        else if (first == send) await send;
    }
    catch (Exception ex)
    {
        queue.Writer.TryWrite(new("error", Text: "ETW 采集异常：" + ex.Message));
    }
    finally
    {
        monitor.Stop();
        lifetime.Cancel();
        await statistics;
        try { await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        var finalProcesses = monitor.GetProcessSnapshot()!;
        await queue.Writer.WriteAsync(new("completed", Text: "监控已结束", EtwLost: monitor.EventsLost, QueueLost: Interlocked.Read(ref queueLost),
            ActiveProcesses: finalProcesses.ActiveCount, Processes: finalProcesses.Processes)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        queue.Writer.TryComplete();
        await send.WaitAsync(TimeSpan.FromSeconds(10));
    }
    return 0;
}
catch (Exception)
{
    return 5;
}
