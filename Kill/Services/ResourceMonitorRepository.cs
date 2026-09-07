using System.Globalization;
using Kill.Models;
using Microsoft.Data.Sqlite;

namespace Kill.Services;

public sealed class ResourceMonitorRepository
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public ResourceMonitorRepository(string? databasePath = null)
    {
        _databasePath = databasePath ?? KillPaths.ResourceDatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("资源监控数据库路径无效。");
        Directory.CreateDirectory(parent);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var schemaVersion = await GetSchemaVersionAsync(connection, cancellationToken);
        if (schemaVersion > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"资源监控数据库版本 {schemaVersion} 高于当前程序支持的版本 {CurrentSchemaVersion}。请升级 Kill Control 后重试。");

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS monitor_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT,
                interval_seconds INTEGER NOT NULL,
                selected_metrics TEXT NOT NULL,
                app_version TEXT NOT NULL,
                duration_seconds INTEGER,
                stop_reason TEXT
            );

            CREATE TABLE IF NOT EXISTS resource_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id INTEGER NOT NULL,
                captured_at_utc TEXT NOT NULL,
                sample_duration_seconds REAL NOT NULL,
                cpu_percent REAL,
                memory_used_bytes INTEGER,
                memory_total_bytes INTEGER,
                memory_percent REAL,
                disk_active_percent REAL,
                disk_read_bytes_per_second REAL,
                disk_write_bytes_per_second REAL,
                network_received_bytes_per_second REAL,
                network_sent_bytes_per_second REAL,
                FOREIGN KEY(session_id) REFERENCES monitor_sessions(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_resource_samples_captured_at
            ON resource_samples(captured_at_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_resource_samples_session
            ON resource_samples(session_id, captured_at_utc DESC);

            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (schemaVersion < CurrentSchemaVersion)
            await UpgradeSchemaAsync(connection, cancellationToken);
        else
            await ValidateCurrentSchemaAsync(connection, cancellationToken);
    }

    public async Task<long> StartSessionAsync(ResourceMonitorOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO monitor_sessions
                (started_at_utc, interval_seconds, selected_metrics, app_version, duration_seconds)
            VALUES ($started, $interval, $metrics, $version, $duration);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$interval", checked((int)options.Interval.TotalSeconds));
        command.Parameters.AddWithValue("$metrics", options.MetricsValue);
        command.Parameters.AddWithValue("$version", ProductInfo.Version);
        AddNullable(command, "$duration", options.Duration is null
            ? null
            : checked((int)options.Duration.Value.TotalSeconds));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task EndSessionAsync(long sessionId, string? stopReason = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE monitor_sessions
            SET ended_at_utc = $ended, stop_reason = $reason
            WHERE id = $id AND ended_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.ToString("O"));
        AddNullable(command, "$reason", stopReason);
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> SaveSampleAsync(ResourceSample sample, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO resource_samples (
                session_id, captured_at_utc, sample_duration_seconds, cpu_percent,
                memory_used_bytes, memory_total_bytes, memory_percent,
                disk_active_percent, disk_read_bytes_per_second, disk_write_bytes_per_second,
                network_received_bytes_per_second, network_sent_bytes_per_second)
            VALUES (
                $session, $captured, $duration, $cpu,
                $memoryUsed, $memoryTotal, $memoryPercent,
                $diskActive, $diskRead, $diskWrite, $networkReceived, $networkSent);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$session", sample.SessionId);
        command.Parameters.AddWithValue("$captured", sample.CapturedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$duration", sample.SampleDurationSeconds);
        AddNullable(command, "$cpu", sample.CpuPercent);
        AddNullable(command, "$memoryUsed", sample.MemoryUsedBytes);
        AddNullable(command, "$memoryTotal", sample.MemoryTotalBytes);
        AddNullable(command, "$memoryPercent", sample.MemoryPercent);
        AddNullable(command, "$diskActive", sample.DiskActivePercent);
        AddNullable(command, "$diskRead", sample.DiskReadBytesPerSecond);
        AddNullable(command, "$diskWrite", sample.DiskWriteBytesPerSecond);
        AddNullable(command, "$networkReceived", sample.NetworkReceivedBytesPerSecond);
        AddNullable(command, "$networkSent", sample.NetworkSentBytesPerSecond);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ResourceSample>> LoadRecentSamplesAsync(int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, captured_at_utc, sample_duration_seconds, cpu_percent,
                   memory_used_bytes, memory_total_bytes, memory_percent,
                   disk_active_percent, disk_read_bytes_per_second, disk_write_bytes_per_second,
                   network_received_bytes_per_second, network_sent_bytes_per_second
            FROM resource_samples
            ORDER BY captured_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var result = new List<ResourceSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ResourceSample
            {
                Id = reader.GetInt64(0),
                SessionId = reader.GetInt64(1),
                CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                SampleDurationSeconds = reader.GetDouble(3),
                CpuPercent = GetNullableDouble(reader, 4),
                MemoryUsedBytes = GetNullableInt64(reader, 5),
                MemoryTotalBytes = GetNullableInt64(reader, 6),
                MemoryPercent = GetNullableDouble(reader, 7),
                DiskActivePercent = GetNullableDouble(reader, 8),
                DiskReadBytesPerSecond = GetNullableDouble(reader, 9),
                DiskWriteBytesPerSecond = GetNullableDouble(reader, 10),
                NetworkReceivedBytesPerSecond = GetNullableDouble(reader, 11),
                NetworkSentBytesPerSecond = GetNullableDouble(reader, 12)
            });
        }
        return result;
    }

    public async Task<long> GetSampleCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM resource_samples;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<bool> HasHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM monitor_sessions LIMIT 1);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var secureDelete = connection.CreateCommand())
        {
            secureDelete.CommandText = "PRAGMA secure_delete = ON;";
            await secureDelete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM resource_samples;
            DELETE FROM monitor_sessions;
            DELETE FROM sqlite_sequence WHERE name IN ('resource_samples', 'monitor_sessions');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        command.Transaction = null;
        command.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task UpgradeSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var columns = await GetSessionColumnsAsync(connection, transaction, cancellationToken);

        if (!columns.Contains("duration_seconds"))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "ALTER TABLE monitor_sessions ADD COLUMN duration_seconds INTEGER;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("stop_reason"))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "ALTER TABLE monitor_sessions ADD COLUMN stop_reason TEXT;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ValidateCurrentSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await GetSessionColumnsAsync(connection, null, cancellationToken);
        if (!columns.Contains("duration_seconds") || !columns.Contains("stop_reason"))
            throw new InvalidDataException("资源监控数据库结构不完整，无法安全读取。请保留数据库并升级或修复后重试。");
    }

    private static async Task<HashSet<string>> GetSessionColumnsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(monitor_sessions);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static double? GetNullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
