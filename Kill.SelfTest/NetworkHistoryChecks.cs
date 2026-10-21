using Kill.Models;
using Kill.Services;
using Microsoft.Data.Sqlite;
using ProcessSentinel.Core;
using ProcessSentinel.Core.Networking;

internal static class NetworkHistoryChecks
{
    internal static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("网络历史重开后保留且快照更新不重复", () => WithRepository(async (repository, path) =>
        {
            var time = DateTimeOffset.UtcNow;
            string session = Guid.NewGuid().ToString("N");
            var tcp = Flow("tcp", time);
            var udp = Flow("udp", time) with { Endpoint = Flow("udp", time).Endpoint with { Protocol = NetworkTransport.UDP, RemotePort = 53 } };
            var bound = Flow("bound", time) with { Endpoint = tcp.Endpoint with { RemoteAddress = "", RemotePort = 0, State = "LISTEN" } };
            await repository.SaveAsync(session, Snapshot(time, [tcp, udp, bound]));
            await repository.SaveAsync(session, Snapshot(time.AddSeconds(10), [tcp with { SentBytes = 999, ReceivedBytes = 1234, EndedAtUtc = time.AddSeconds(9) }, udp]));
            SqliteConnection.ClearAllPools();
            var reopened = new NetworkHistoryRepository(path);
            var processes = await reopened.LoadProcessesAsync();
            Require(processes.TotalCount == 1 && processes.Rows[0].ConnectionCount == 2, "重复快照或监听端点生成了多余记录");
            var records = await reopened.LoadConnectionsAsync(processes.Rows[0].Id);
            var saved = records.Rows.Single(row => row.Id == "tcp");
            Require(saved.SentBytes == 999 && saved.ReceivedBytes == 1234 && saved.RemoteAddress == "203.0.113.10" && saved.RemotePort == 443 &&
                saved.FirstSeenUtc == time && saved.EndedAtUtc == time.AddSeconds(9), "历史端点、首次时间、最终时间或累计字节错误");
            await repository.SaveAsync(session, Snapshot(time.AddSeconds(20), []));
            Require((await reopened.LoadConnectionsAsync(processes.Rows[0].Id)).TotalCount == 2, "实时列表清空删除了历史");
        }));
        yield return ("网络历史隔离 PID 重用与不同监控会话", () => WithRepository(async (repository, _) =>
        {
            var time = DateTimeOffset.UtcNow;
            string session = Guid.NewGuid().ToString("N");
            var first = Flow("one", time);
            var reused = Flow("two", time) with { Process = first.Process with { StartTimeUtcTicks = time.AddSeconds(-1).UtcTicks } };
            await repository.SaveAsync(session, Snapshot(time, [first, reused]));
            await repository.SaveAsync(Guid.NewGuid().ToString("N"), Snapshot(time, [first]));
            var owners = await repository.LoadProcessesAsync();
            Require(owners.TotalCount == 3 && owners.Rows.All(row => row.ConnectionCount == 1), "PID 或跨会话记录被错误合并");
            foreach (var owner in owners.Rows) Require((await repository.LoadConnectionsAsync(owner.Id)).TotalCount == 1, "进程历史发生串入");
        }));
        yield return ("网络历史分页、排序、进程定位与字面搜索", () => WithRepository(async (repository, _) =>
        {
            var time = DateTimeOffset.UtcNow;
            string session = Guid.NewGuid().ToString("N");
            var flows = Enumerable.Range(0, 205).Select(index => Flow(index.ToString("D3"), time.AddSeconds(index)) with
            { Endpoint = Flow("", time).Endpoint with { RemotePort = index + 1000 } }).ToArray();
            await repository.SaveAsync(session, Snapshot(time.AddSeconds(205), flows));
            var later = Flow("later", time.AddSeconds(300)) with
            {
                Process = flows[0].Process with { Id = 22222, Name = "Other" },
                Endpoint = flows[0].Endpoint with { ProcessId = 22222 }
            };
            await repository.SaveAsync(Guid.NewGuid().ToString("N"), Snapshot(time.AddSeconds(300), [later]));
            var preferred = new NetworkHistoryProcessKey(session, flows[0].Process.Id, flows[0].Process.StartTimeUtcTicks);
            // All original flows must share a process instance despite differing observation times.
            var processes = await repository.LoadProcessesAsync(preferred: preferred);
            Require(processes.Rows[0].Key == preferred, "所选实时进程未定位到对应历史");
            long owner = processes.Rows[0].Id;
            var one = await repository.LoadConnectionsAsync(owner);
            var two = await repository.LoadConnectionsAsync(owner, offset: 100);
            var three = await repository.LoadConnectionsAsync(owner, offset: 200);
            Require(one.TotalCount == 205 && one.Rows.Count == 100 && two.Rows.Count == 100 && three.Rows.Count == 5 &&
                one.Rows.Concat(two.Rows).Concat(three.Rows).Select(row => row.Id).Distinct().Count() == 205 && one.Rows[0].Id == "204", "分页重复、漏项或排序错误");
            Require((await repository.LoadConnectionsAsync(owner, "1204")).Rows.Single().RemotePort == 1204, "端口筛选失败");
            Require((await repository.LoadProcessesAsync("%_' OR 1=1")).TotalCount == 0, "查询被当成 SQL 或通配符执行");
        }));
        yield return ("网络历史保留 IPv6 地址和协议差异", () => WithRepository(async (repository, _) =>
        {
            var time = DateTimeOffset.UtcNow;
            var tcp = Flow("tcp", time) with { Endpoint = Flow("", time).Endpoint with { RemoteAddress = "2001:db8::1" } };
            var udp = tcp with { Id = "udp", Endpoint = tcp.Endpoint with { Protocol = NetworkTransport.UDP } };
            await repository.SaveAsync(Guid.NewGuid().ToString("N"), Snapshot(time, [tcp, udp]));
            long owner = (await repository.LoadProcessesAsync()).Rows.Single().Id;
            var rows = await repository.LoadConnectionsAsync(owner, "2001:db8");
            Require(rows.TotalCount == 2 && rows.Rows.Select(row => row.Protocol).Distinct().Count() == 2 &&
                (await repository.LoadConnectionsAsync(owner, "UDP")).Rows.Single().Protocol == "UDP", "IPv6 或同端点协议记录被合并");
        }));
        yield return ("网络历史拒绝未来数据库且保留原版本", () => WithRepository(async (_, path) =>
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 99;";
                command.ExecuteNonQuery();
            }
            try { await new NetworkHistoryRepository(path).LoadProcessesAsync(); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("高于当前"))
            {
                using var connection = new SqliteConnection($"Data Source={path}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                Require((long)command.ExecuteScalar()! == 99, "拒绝读取时改写了未来结构版本");
                return;
            }
            throw new InvalidOperationException("接受了未来网络历史结构");
        }));
        yield return ("网络历史支持取消且不会创建数据库", async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "Kill-history-cancel-" + Guid.NewGuid().ToString("N"), "history.db");
            using var token = new CancellationTokenSource();
            token.Cancel();
            try { await new NetworkHistoryRepository(path).LoadProcessesAsync(token: token.Token); }
            catch (OperationCanceledException) { Require(!File.Exists(path), "取消的查询仍写入了磁盘"); return; }
            throw new InvalidOperationException("未取消历史读取");
        }
        );
    }

    private static async Task WithRepository(Func<NetworkHistoryRepository, string, Task> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Kill-network-history-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "history.db");
        Directory.CreateDirectory(directory);
        try { await action(new NetworkHistoryRepository(path), path); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static NetworkFlow Flow(string id, DateTimeOffset time) => new(id,
        new(NetworkTransport.TCP, 12345, "192.0.2.1", 50000, "203.0.113.10", 443, "ESTABLISHED"),
        new(12345, 0, "History Example", @"C:\Example\app.exe", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks),
        time, time, null, 100, 200, 0, 0);
    private static NetworkSnapshot Snapshot(DateTimeOffset time, IReadOnlyList<NetworkFlow> flows) => new(time.AddMinutes(-1), time, true, flows, 0, 0, 0, 0);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
