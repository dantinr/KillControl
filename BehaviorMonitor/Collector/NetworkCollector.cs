using System.IO.Pipes;
using ProcessSentinel.Core;
using ProcessSentinel.Core.Networking;

internal static class NetworkCollector
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--network-pipe" || !Guid.TryParseExact(args[1], "N", out _) ||
            args[2] != "--parent" || !int.TryParse(args[3], out int parentId) || parentId <= 0) return 2;
        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", "Kill-Network-" + args[1], PipeDirection.InOut,
            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(20000);
            LocalPipe.VerifyServer(pipe, parentId);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var monitor = new NetworkEtwMonitor();
            try { monitor.Start(); }
            catch (Exception exception)
            {
                await writer.WriteLineAsync(Protocol.Serialize(new NetworkMessage("error", Error: exception.Message)));
                return 4;
            }
            await writer.WriteLineAsync(Protocol.Serialize(new NetworkMessage("ready", monitor.Capture())));
            var publish = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                try
                {
                    while (await timer.WaitForNextTickAsync(lifetime.Token))
                        await writer.WriteLineAsync(Protocol.Serialize(new NetworkMessage("snapshot", monitor.Capture())));
                }
                catch (OperationCanceledException) { }
            });
            var commands = Task.Run(async () =>
            {
                while (await reader.ReadLineAsync(lifetime.Token) is { } line) if (line == "stop") return;
            });
            try
            {
                var completed = await Task.WhenAny(commands, publish, monitor.Completion);
                await completed;
            }
            finally
            {
                monitor.Stop();
                lifetime.Cancel();
                try { await publish.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
                await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await writer.WriteLineAsync(Protocol.Serialize(new NetworkMessage("completed", monitor.Capture(false))));
            return 0;
        }
        catch { return 5; }
    }
}
