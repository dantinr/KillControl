using System.Net;
using Kill.Models;
using Microsoft.Data.Sqlite;
using ProcessSentinel.Core.Networking;

namespace Kill.Services;

public sealed class NetworkHistoryRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    public string DatabasePath { get; }

    public NetworkHistoryRepository(string? databasePath = null) => DatabasePath = databasePath ?? KillPaths.NetworkHistoryDatabasePath;

    public Task SaveAsync(string sessionId, NetworkSnapshot snapshot, CancellationToken token = default) => RunAsync(connection =>
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("无效的网络监控会话标识。", nameof(sessionId));
        var flows = snapshot.Flows.Where(flow => flow.Endpoint.ProcessId > 0 && flow.Endpoint.RemotePort is > 0 and <= 65535 &&
            IPAddress.TryParse(flow.Endpoint.RemoteAddress, out var remote) && !remote.Equals(IPAddress.Any) && !remote.Equals(IPAddress.IPv6Any)).ToArray();
        using var transaction = connection.BeginTransaction();
        var processIds = new Dictionary<(int Id, long Start), long>();
        foreach (var group in flows.GroupBy(flow => (flow.Process.Id, flow.Process.StartTimeUtcTicks)))
        {
            token.ThrowIfCancellationRequested();
            var process = group.First().Process;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO network_processes(session_id, pid, start_ticks, name, path, first_seen, last_seen)
                VALUES($session, $pid, $start, $name, $path, $first, $last)
                ON CONFLICT(session_id, pid, start_ticks) DO UPDATE SET
                    name = excluded.name, path = CASE WHEN excluded.path <> '' THEN excluded.path ELSE path END,
                    first_seen = MIN(first_seen, excluded.first_seen), last_seen = MAX(last_seen, excluded.last_seen)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$pid", process.Id);
            command.Parameters.AddWithValue("$start", process.StartTimeUtcTicks);
            command.Parameters.AddWithValue("$name", process.Name);
            command.Parameters.AddWithValue("$path", process.Path);
            command.Parameters.AddWithValue("$first", group.Min(flow => flow.FirstSeenUtc.UtcTicks));
            command.Parameters.AddWithValue("$last", group.Max(flow => (flow.EndedAtUtc ?? snapshot.CapturedAtUtc).UtcTicks));
            processIds[group.Key] = (long)command.ExecuteScalar()!;
        }
        using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO network_connections(session_id, flow_id, owner_id, protocol, local_address, local_port,
                remote_address, remote_port, state, first_seen, last_seen, last_activity, ended, sent, received)
            VALUES($session, $flow, $owner, $protocol, $local, $localPort, $remote, $remotePort, $state, $first, $last, $activity, $ended, $sent, $received)
            ON CONFLICT(session_id, flow_id) DO UPDATE SET
                state = excluded.state, last_seen = MAX(last_seen, excluded.last_seen), last_activity = MAX(last_activity, excluded.last_activity),
                ended = COALESCE(excluded.ended, ended), sent = MAX(sent, excluded.sent), received = MAX(received, excluded.received);
            """;
        foreach (string name in new[] { "$session", "$flow", "$owner", "$protocol", "$local", "$localPort", "$remote", "$remotePort", "$state", "$first", "$last", "$activity", "$ended", "$sent", "$received" })
            save.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        foreach (var flow in flows)
        {
            token.ThrowIfCancellationRequested();
            var endpoint = flow.Endpoint;
            save.Parameters["$session"].Value = sessionId;
            save.Parameters["$flow"].Value = flow.Id;
            save.Parameters["$owner"].Value = processIds[(flow.Process.Id, flow.Process.StartTimeUtcTicks)];
            save.Parameters["$protocol"].Value = endpoint.Protocol.ToString();
            save.Parameters["$local"].Value = NetworkAddress.Normalize(endpoint.LocalAddress);
            save.Parameters["$localPort"].Value = endpoint.LocalPort;
            save.Parameters["$remote"].Value = NetworkAddress.Normalize(endpoint.RemoteAddress);
            save.Parameters["$remotePort"].Value = endpoint.RemotePort;
            save.Parameters["$state"].Value = endpoint.State;
            save.Parameters["$first"].Value = flow.FirstSeenUtc.UtcTicks;
            save.Parameters["$last"].Value = (flow.EndedAtUtc ?? snapshot.CapturedAtUtc).UtcTicks;
            save.Parameters["$activity"].Value = flow.LastActivityUtc.UtcTicks;
            save.Parameters["$ended"].Value = flow.EndedAtUtc is { } ended ? ended.UtcTicks : DBNull.Value;
            save.Parameters["$sent"].Value = flow.SentBytes;
            save.Parameters["$received"].Value = flow.ReceivedBytes;
            save.ExecuteNonQuery();
        }
        transaction.Commit();
        return 0;
    }, token);

    public Task<NetworkHistoryPage<NetworkHistoryProcess>> LoadProcessesAsync(string query = "", int offset = 0, int limit = 100,
        NetworkHistoryProcessKey? preferred = null, CancellationToken token = default) => RunAsync(connection =>
    {
        using var command = connection.CreateCommand();
        const string filter = " WHERE instr(lower(p.name || ' ' || p.path || ' ' || p.pid), lower($query)) > 0";
        command.CommandText = "SELECT COUNT(*) FROM network_processes p " + filter;
        command.Parameters.AddWithValue("$query", query.Trim());
        long total = (long)command.ExecuteScalar()!;
        command.CommandText = """
            SELECT p.id, p.session_id, p.pid, p.start_ticks, p.name, p.path, p.first_seen, p.last_seen,
                (SELECT COUNT(*) FROM network_connections c WHERE c.owner_id = p.id)
            FROM network_processes p
            """ + filter + """
             ORDER BY (p.session_id = $session AND p.pid = $pid AND p.start_ticks = $start) DESC, p.last_seen DESC, p.id DESC
             LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$session", preferred?.SessionId ?? "");
        command.Parameters.AddWithValue("$pid", preferred?.ProcessId ?? -1);
        command.Parameters.AddWithValue("$start", preferred?.StartTicks ?? -1);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var rows = new List<NetworkHistoryProcess>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new(reader.GetInt64(0), new(reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)), reader.GetString(4), reader.GetString(5),
                Time(reader.GetInt64(6)), Time(reader.GetInt64(7)), reader.GetInt64(8)));
        }
        return new NetworkHistoryPage<NetworkHistoryProcess>(rows, total);
    }, token);

    public Task<NetworkHistoryPage<NetworkHistoryConnection>> LoadConnectionsAsync(long ownerId, string query = "", int offset = 0, int limit = 100,
        CancellationToken token = default) => RunAsync(connection =>
    {
        using var command = connection.CreateCommand();
        const string filter = " WHERE owner_id = $owner AND instr(lower(remote_address || ' ' || remote_port || ' ' || protocol), lower($query)) > 0";
        command.CommandText = "SELECT COUNT(*) FROM network_connections " + filter;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$query", query.Trim());
        long total = (long)command.ExecuteScalar()!;
        command.CommandText = """
            SELECT flow_id, protocol, local_address, local_port, remote_address, remote_port, state,
                first_seen, last_seen, last_activity, ended, sent, received
            FROM network_connections
            """ + filter + " ORDER BY first_seen DESC, flow_id LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var rows = new List<NetworkHistoryConnection>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5),
                reader.GetString(6), Time(reader.GetInt64(7)), Time(reader.GetInt64(8)), Time(reader.GetInt64(9)), reader.IsDBNull(10) ? null : Time(reader.GetInt64(10)),
                reader.GetInt64(11), reader.GetInt64(12)));
        }
        return new NetworkHistoryPage<NetworkHistoryConnection>(rows, total);
    }, token);

    private Task<T> RunAsync<T>(Func<SqliteConnection, T> action, CancellationToken token) => Task.Run(() =>
    {
        gate.Wait(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
            connection.Open();
            if (!initialized)
            {
                using var setup = connection.CreateCommand();
                setup.CommandText = "PRAGMA user_version;";
                if ((long)setup.ExecuteScalar()! > 1) throw new InvalidOperationException("网络历史数据库版本高于当前程序支持的版本，请升级程序。");
                setup.CommandText = """
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE IF NOT EXISTS network_processes (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, pid INTEGER NOT NULL, start_ticks INTEGER NOT NULL,
                        name TEXT NOT NULL, path TEXT NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                        UNIQUE(session_id, pid, start_ticks));
                    CREATE TABLE IF NOT EXISTS network_connections (
                        session_id TEXT NOT NULL, flow_id TEXT NOT NULL, owner_id INTEGER NOT NULL REFERENCES network_processes(id),
                        protocol TEXT NOT NULL, local_address TEXT NOT NULL, local_port INTEGER NOT NULL,
                        remote_address TEXT NOT NULL, remote_port INTEGER NOT NULL, state TEXT NOT NULL,
                        first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, last_activity INTEGER NOT NULL, ended INTEGER,
                        sent INTEGER NOT NULL, received INTEGER NOT NULL, PRIMARY KEY(session_id, flow_id));
                    CREATE INDEX IF NOT EXISTS ix_network_process_seen ON network_processes(last_seen DESC, id DESC);
                    CREATE INDEX IF NOT EXISTS ix_network_connection_owner ON network_connections(owner_id, first_seen DESC, flow_id);
                    PRAGMA user_version = 1;
                    """;
                setup.ExecuteNonQuery();
                initialized = true;
            }
            token.ThrowIfCancellationRequested();
            return action(connection);
        }
        finally { gate.Release(); }
    }, token);

    private static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
}
