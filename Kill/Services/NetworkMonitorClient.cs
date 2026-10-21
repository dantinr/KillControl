using System.Diagnostics;
using System.IO.Pipes;
using ProcessSentinel.Core;
using ProcessSentinel.Core.Networking;

namespace Kill.Services;

public sealed class NetworkMonitorClient : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream? pipe;
    private StreamWriter? commands;
    private Task? receive;
    private NetworkSnapshot? snapshot;
    private volatile bool running;
    public bool Running => running;
    public NetworkSnapshot? Snapshot => Volatile.Read(ref snapshot);
    public string Error { get; private set; } = "";

    public async Task StartAsync()
    {
        string collectorPath = Path.Combine(AppContext.BaseDirectory, "ProcessSentinel.Collector.exe");
        if (!File.Exists(collectorPath)) throw new FileNotFoundException("缺少内部采集组件，请保留运行目录中的文件。", collectorPath);
        string id = Guid.NewGuid().ToString("N");
        pipe = LocalPipe.CreateServer("Kill-Network-" + id);
        using var collector = await Task.Run(() => Process.Start(new ProcessStartInfo(collectorPath,
            $"--network-pipe {id} --parent {Environment.ProcessId}")
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
            ?? throw new InvalidOperationException("无法启动网络采集组件。");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        await pipe.WaitForConnectionAsync(startup.Token);
        LocalPipe.VerifyClient(pipe, collector.Id);
        commands = new(pipe, leaveOpen: true) { AutoFlush = true };
        receive = Task.Run(ReceiveAsync);
        await ready.Task.WaitAsync(startup.Token);
    }

    private async Task ReceiveAsync()
    {
        bool completed = false;
        try
        {
            using var reader = new StreamReader(pipe!, leaveOpen: true);
            while (await reader.ReadLineAsync(lifetime.Token) is { } line)
            {
                var message = Protocol.Deserialize<NetworkMessage>(line);
                if (message is null) continue;
                if (message.Type == "error") throw new IOException(message.Error ?? "网络采集失败。");
                if (message.Snapshot is { } update) Volatile.Write(ref snapshot, update);
                if (message.Type == "ready") { running = true; ready.TrySetResult(); }
                if (message.Type == "completed") completed = true;
            }
            if (!completed) throw new IOException("采集组件意外断开，已收到的流量可能不完整。");
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception exception) { Error = exception.Message; ready.TrySetException(exception); }
        finally
        {
            if (!completed && Snapshot is { Running: true } last)
            {
                var time = DateTimeOffset.UtcNow;
                Volatile.Write(ref snapshot, last with
                {
                    Running = false,
                    CapturedAtUtc = time,
                    SendBytesPerSecond = 0,
                    ReceiveBytesPerSecond = 0,
                    Warning = Error,
                    Flows = last.Flows.Select(flow => flow with
                    {
                        EndedAtUtc = flow.EndedAtUtc ?? time,
                        SendBytesPerSecond = 0,
                        ReceiveBytesPerSecond = 0,
                        Endpoint = flow.EndedAtUtc is null ? flow.Endpoint with { State = "COLLECTOR_INTERRUPTED" } : flow.Endpoint
                    }).ToArray()
                });
            }
            running = false;
            pipe?.Dispose();
        }
    }

    public void CancelStartup() => lifetime.Cancel();

    public async Task StopAsync()
    {
        if (receive is null) lifetime.Cancel();
        if (commands is not null && running)
        {
            try { await commands.WriteLineAsync("stop"); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
        if (receive is not null)
        {
            try { await receive.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException)
            {
                Error = "网络采集停止超时，统计可能不完整。";
                lifetime.Cancel();
                pipe?.Dispose();
                await receive;
            }
        }
        running = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lifetime.Cancel();
        try { commands?.Dispose(); } catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        pipe?.Dispose();
        lifetime.Dispose();
    }
}
