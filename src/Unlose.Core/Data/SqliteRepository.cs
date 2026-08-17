using System.Text.Json;
using Unlose.Core.Enums;
using Unlose.Core.Models;
using Microsoft.Data.Sqlite;

namespace Unlose.Core.Data;

/// <summary>SQLite data access layer</summary>
public class SqliteRepository
{
    private readonly string _dbPath;

    public SqliteRepository(string dbPath)
    {
        _dbPath = dbPath;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);
        return conn;
    }

    // ── Snapshots ────────────────────────────────────────────────────────────

    public async Task UpsertSnapshotAsync(SnapshotRecord r, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots
                (id, created_at, trigger_type, trigger_detail, label, volumes_json,
                 size_bytes, shadow_id, device_object, integrity_hash,
                 is_pinned, notes, session_id)
            VALUES
                (@id, @ca, @tt, @td, @lb, @vj,
                 @sb, @si, @do, @ih,
                 @ip, @nt, @sid)
            ON CONFLICT(id) DO UPDATE SET
                is_pinned      = excluded.is_pinned,
                notes          = excluded.notes,
                size_bytes     = excluded.size_bytes,
                integrity_hash = excluded.integrity_hash,
                session_id     = excluded.session_id;
            """;
        cmd.Parameters.AddWithValue("@id", r.Id.ToString());
        cmd.Parameters.AddWithValue("@ca", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@tt", r.TriggerType.ToString());
        cmd.Parameters.AddWithValue("@td", (object?)r.TriggerDetail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lb", (object?)r.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vj", JsonSerializer.Serialize(r.Volumes));
        cmd.Parameters.AddWithValue("@sb", r.SizeBytes);
        cmd.Parameters.AddWithValue("@si", (object?)r.ShadowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@do", (object?)r.DeviceObject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ih", (object?)r.IntegrityHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", r.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("@nt", (object?)r.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sid", (object?)r.SessionId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<SnapshotRecord>> ListSnapshotsAsync(
        TriggerType? filterByType = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = filterByType.HasValue
            ? "SELECT * FROM snapshots WHERE trigger_type=@tt ORDER BY created_at DESC;"
            : "SELECT * FROM snapshots ORDER BY created_at DESC;";
        if (filterByType.HasValue)
            cmd.Parameters.AddWithValue("@tt", filterByType.Value.ToString());

        var list = new List<SnapshotRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadSnapshot(reader));
        return list;
    }

    /// <summary>
    /// Actively runs a WAL checkpoint (TRUNCATE mode: merges WAL contents back into the main database and truncates the WAL file).
    /// Called periodically by WalCheckpointService to prevent unbounded WAL growth; also used before diagnostics to ensure external tools can read a consistent view.
    /// </summary>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SnapshotRecord?> GetSnapshotAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM snapshots WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSnapshot(reader) : null;
    }

    public async Task DeleteSnapshotAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM snapshots WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SnapshotRecord ReadSnapshot(SqliteDataReader r)
    {
        var volumes = JsonSerializer.Deserialize<string[]>(r.GetString(r.GetOrdinal("volumes_json"))) ?? [];
        return new SnapshotRecord
        {
            Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
            Volumes = volumes,
            CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
            TriggerType = Enum.Parse<TriggerType>(r.GetString(r.GetOrdinal("trigger_type"))),
            TriggerDetail = r.IsDBNull(r.GetOrdinal("trigger_detail")) ? null : r.GetString(r.GetOrdinal("trigger_detail")),
            Label = r.IsDBNull(r.GetOrdinal("label")) ? null : r.GetString(r.GetOrdinal("label")),
            SizeBytes = r.GetInt64(r.GetOrdinal("size_bytes")),
            ShadowId = r.IsDBNull(r.GetOrdinal("shadow_id")) ? null : r.GetString(r.GetOrdinal("shadow_id")),
            DeviceObject = r.IsDBNull(r.GetOrdinal("device_object")) ? null : r.GetString(r.GetOrdinal("device_object")),
            IntegrityHash = r.IsDBNull(r.GetOrdinal("integrity_hash")) ? null : r.GetString(r.GetOrdinal("integrity_hash")),
            IsPinned = r.GetInt32(r.GetOrdinal("is_pinned")) != 0,
            Notes = r.IsDBNull(r.GetOrdinal("notes")) ? null : r.GetString(r.GetOrdinal("notes")),
            SessionId = r.IsDBNull(r.GetOrdinal("session_id")) ? null : r.GetString(r.GetOrdinal("session_id")),
        };
    }

    // ── Monitor Events ──────────────────────────────────────────────────────

    public async Task InsertMonitorEventAsync(MonitorEventRecord e, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO monitor_events (id, occurred_at, event_type, process_name, pid, command_line, description, rule_id, severity)
            VALUES (@id, @oa, @et, @pn, @pi, @cl, @dc, @ri, @sv);
            """;
        cmd.Parameters.AddWithValue("@id", e.Id.ToString());
        cmd.Parameters.AddWithValue("@oa", e.OccurredAt.ToString("O"));
        cmd.Parameters.AddWithValue("@et", e.EventType);
        cmd.Parameters.AddWithValue("@pn", e.ProcessName);
        cmd.Parameters.AddWithValue("@pi", e.Pid);
        cmd.Parameters.AddWithValue("@cl", (object?)e.CommandLine ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dc", e.Description);
        cmd.Parameters.AddWithValue("@ri", (object?)e.RuleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sv", e.Severity.HasValue ? e.Severity.Value.ToString() : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<MonitorEventRecord>> ListMonitorEventsAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? eventType = null,
        int maxResults = 200,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var conditions = new List<string>();
        if (from.HasValue) { conditions.Add("occurred_at >= @fr"); cmd.Parameters.AddWithValue("@fr", from.Value.ToString("O")); }
        if (to.HasValue) { conditions.Add("occurred_at <= @to"); cmd.Parameters.AddWithValue("@to", to.Value.ToString("O")); }
        if (eventType != null) { conditions.Add("event_type = @et"); cmd.Parameters.AddWithValue("@et", eventType); }
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"SELECT * FROM monitor_events {where} ORDER BY occurred_at DESC LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", maxResults);

        var list = new List<MonitorEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new MonitorEventRecord
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                OccurredAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("occurred_at"))),
                EventType = reader.GetString(reader.GetOrdinal("event_type")),
                ProcessName = reader.GetString(reader.GetOrdinal("process_name")),
                Pid = reader.GetInt32(reader.GetOrdinal("pid")),
                CommandLine = reader.IsDBNull(reader.GetOrdinal("command_line")) ? null : reader.GetString(reader.GetOrdinal("command_line")),
                Description = reader.GetString(reader.GetOrdinal("description")),
                RuleId = reader.IsDBNull(reader.GetOrdinal("rule_id")) ? null : reader.GetString(reader.GetOrdinal("rule_id")),
                Severity = reader.IsDBNull(reader.GetOrdinal("severity")) ? null : Enum.Parse<DangerSeverity>(reader.GetString(reader.GetOrdinal("severity")))
            });
        }
        return list;
    }

    public async Task<List<AgentSessionRecord>> ListAgentSessionsAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = activeOnly
            ? "SELECT id, process_name, pid, started_at, ended_at, pre_session_snapshot FROM agent_sessions WHERE ended_at IS NULL ORDER BY started_at DESC;"
            : "SELECT id, process_name, pid, started_at, ended_at, pre_session_snapshot FROM agent_sessions ORDER BY started_at DESC;";

        var list = new List<AgentSessionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AgentSessionRecord
            {
                Id = Guid.Parse(reader.GetString(0)),
                ProcessName = reader.GetString(1),
                Pid = reader.GetInt32(2),
                StartedAt = DateTime.Parse(reader.GetString(3)),
                EndedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                PreSessionSnapshotId = reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5))
            });
        }

        return list;
    }

    // ── Pending Replacements ────────────────────────────────────────────────

    public async Task AddPendingReplacementAsync(Guid id, string stagingPath, string targetPath, Guid snapshotId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO pending_replacements (id, staging_path, target_path, snapshot_id, created_at) VALUES (@id, @sp, @tp, @si, @ca);";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@sp", stagingPath);
        cmd.Parameters.AddWithValue("@tp", targetPath);
        cmd.Parameters.AddWithValue("@si", snapshotId.ToString());
        cmd.Parameters.AddWithValue("@ca", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(Guid Id, string StagingPath, string TargetPath, Guid SnapshotId)>> ListPendingReplacementsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, staging_path, target_path, snapshot_id FROM pending_replacements ORDER BY created_at;";
        var list = new List<(Guid, string, string, Guid)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), Guid.Parse(reader.GetString(3))));
        return list;
    }

    public async Task RemovePendingReplacementAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_replacements WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Backup Catalog ──────────────────────────────────────────────────────

    public async Task AddBackupCatalogEntryAsync(Guid id, Guid snapshotId, string backupPath, DateTime createdAt, long sizeBytes, bool isEncrypted, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backup_catalog_entries (id, snapshot_id, backup_path, created_at, size_bytes, is_encrypted)
            VALUES (@id, @sid, @bp, @ca, @sb, @ie)
            ON CONFLICT(id) DO UPDATE SET
                snapshot_id=@sid,
                backup_path=@bp,
                created_at=@ca,
                size_bytes=@sb,
                is_encrypted=@ie;
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@sid", snapshotId.ToString());
        cmd.Parameters.AddWithValue("@bp", backupPath);
        cmd.Parameters.AddWithValue("@ca", createdAt.ToString("O"));
        cmd.Parameters.AddWithValue("@sb", sizeBytes);
        cmd.Parameters.AddWithValue("@ie", isEncrypted ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(Guid Id, Guid SnapshotId, string BackupPath, DateTime CreatedAt, long SizeBytes, bool IsEncrypted)>> ListBackupCatalogEntriesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, snapshot_id, backup_path, created_at, size_bytes, is_encrypted FROM backup_catalog_entries ORDER BY created_at DESC;";
        var list = new List<(Guid, Guid, string, DateTime, long, bool)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add((
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3)),
                reader.GetInt64(4),
                reader.GetInt32(5) != 0));
        }
        return list;
    }

    public async Task RemoveBackupCatalogEntryAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM backup_catalog_entries WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Audit Log ───────────────────────────────────────────────────────────

    public async Task InsertAuditEntryAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_log (id, timestamp, action, actor, details, success)
            VALUES (@id, @ts, @ac, @at, @dt, @sc);
            """;
        cmd.Parameters.AddWithValue("@id", entry.Id.ToString());
        cmd.Parameters.AddWithValue("@ts", entry.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@ac", entry.Action);
        cmd.Parameters.AddWithValue("@at", entry.Actor);
        cmd.Parameters.AddWithValue("@dt", (object?)entry.Details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sc", entry.Success ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AuditEntry>> QueryAuditEntriesAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var conditions = new List<string>();
        if (from.HasValue) { conditions.Add("timestamp >= @fr"); cmd.Parameters.AddWithValue("@fr", from.Value.ToString("O")); }
        if (to.HasValue) { conditions.Add("timestamp <= @to"); cmd.Parameters.AddWithValue("@to", to.Value.ToString("O")); }
        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        cmd.CommandText = $"SELECT id, timestamp, action, actor, details, success FROM audit_log {where} ORDER BY timestamp DESC;";
        var list = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AuditEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                Timestamp = DateTime.Parse(reader.GetString(1)),
                Action = reader.GetString(2),
                Actor = reader.GetString(3),
                Details = reader.IsDBNull(4) ? null : reader.GetString(4),
                Success = reader.GetInt32(5) != 0
            });
        }
        return list;
    }
}
