using Microsoft.Data.Sqlite;

namespace Unlose.Core.Data;

/// <summary>Database schema initializer</summary>
public static class DatabaseInitializer
{
    public static async Task EnsureCreatedAsync(string dbPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);

        // Enable WAL mode to improve concurrent write performance
        await ExecuteAsync(conn, "PRAGMA journal_mode=WAL;", ct);
        await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;", ct);
        // In WAL mode, synchronous=NORMAL is safe and faster (transaction commits do not force fsync; consistency is guaranteed by the WAL);
        // wal_autocheckpoint=1000 (~1000 pages ≈ 4MB) lets SQLite checkpoint automatically, preventing unbounded WAL growth.
        // Previously, without autocheckpoint configured, the WAL piled up beyond 4MB and external processes reading the database directly saw spurious "malformed" errors.
        await ExecuteAsync(conn, "PRAGMA synchronous=NORMAL;", ct);
        await ExecuteAsync(conn, "PRAGMA wal_autocheckpoint=1000;", ct);

        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS snapshots (
                id              TEXT PRIMARY KEY,
                created_at      TEXT NOT NULL,
                trigger_type    TEXT NOT NULL,
                trigger_detail  TEXT,
                label           TEXT,
                volumes_json    TEXT NOT NULL,
                size_bytes      INTEGER NOT NULL DEFAULT 0,
                shadow_id       TEXT,
                device_object   TEXT,
                integrity_hash  TEXT,
                is_pinned       INTEGER NOT NULL DEFAULT 0,
                offsite_status  TEXT NOT NULL DEFAULT 'NotSynced',
                offsite_done_at TEXT,
                notes           TEXT,
                session_id      TEXT
            );
            """, ct);

        // Legacy database migration: add the session_id column to snapshots when missing (idempotency of SQLite ALTER ADD COLUMN is guaranteed by the preceding check)
        if (!await ColumnExistsAsync(conn, "snapshots", "session_id", ct))
            await ExecuteAsync(conn, "ALTER TABLE snapshots ADD COLUMN session_id TEXT;", ct);

        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS monitor_events (
                id           TEXT PRIMARY KEY,
                occurred_at  TEXT NOT NULL,
                event_type   TEXT NOT NULL,
                process_name TEXT NOT NULL,
                pid          INTEGER NOT NULL,
                command_line TEXT,
                description  TEXT NOT NULL,
                rule_id      TEXT,
                severity     TEXT
            );
            """, ct);

        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS agent_sessions (
                id                    TEXT PRIMARY KEY,
                process_name          TEXT NOT NULL,
                pid                   INTEGER NOT NULL,
                started_at            TEXT NOT NULL,
                ended_at              TEXT,
                pre_session_snapshot  TEXT
            );
            """, ct);

        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS pending_replacements (
                id           TEXT PRIMARY KEY,
                staging_path TEXT NOT NULL,
                target_path  TEXT NOT NULL,
                snapshot_id  TEXT NOT NULL,
                created_at   TEXT NOT NULL
            );
            """, ct);

        // Indexes
        await ExecuteAsync(conn, "CREATE INDEX IF NOT EXISTS idx_snapshots_created ON snapshots(created_at DESC);", ct);
        await ExecuteAsync(conn, "CREATE INDEX IF NOT EXISTS idx_monitor_events_occurred ON monitor_events(occurred_at DESC);", ct);

        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS backup_catalog_entries (
                id           TEXT PRIMARY KEY,
                snapshot_id  TEXT NOT NULL,
                backup_path  TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                size_bytes   INTEGER NOT NULL,
                is_encrypted INTEGER NOT NULL
            );
            """, ct);

        await ExecuteAsync(conn, """
            CREATE TABLE IF NOT EXISTS audit_log (
                id         TEXT PRIMARY KEY,
                timestamp  TEXT NOT NULL,
                action     TEXT NOT NULL,
                actor      TEXT NOT NULL,
                details    TEXT,
                success    INTEGER NOT NULL
            );
            """, ct);

        await ExecuteAsync(conn, "CREATE INDEX IF NOT EXISTS idx_backup_catalog_created ON backup_catalog_entries(created_at DESC);", ct);
        await ExecuteAsync(conn, "CREATE INDEX IF NOT EXISTS idx_audit_log_timestamp ON audit_log(timestamp DESC);", ct);
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
