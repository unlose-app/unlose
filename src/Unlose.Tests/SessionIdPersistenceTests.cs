using Unlose.Core.Data;
using Unlose.Core.Enums;
using Unlose.Core.Models;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// Snapshot-session association persistence tests (patent claim 15): legacy database migration
/// (ALTER TABLE) of the snapshots.session_id column and read/write round-trips.
/// </summary>
public class SessionIdPersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SessionIdPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "unlose-sessionid-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "snapshots.db");
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SessionId_RoundTripsThroughRepository()
    {
        await DatabaseInitializer.EnsureCreatedAsync(_dbPath);
        var repo = new SqliteRepository(_dbPath);

        var record = new SnapshotRecord
        {
            Volumes = ["C:\\"],
            TriggerType = TriggerType.AgentInitiated,
            TriggerDetail = "via mcp",
            Label = "新会话开始",
            SessionId = "conv-abc-123"
        };
        await repo.UpsertSnapshotAsync(record);

        var loaded = await repo.ListSnapshotsAsync();
        Assert.Single(loaded);
        Assert.Equal("conv-abc-123", loaded[0].SessionId);
        Assert.Equal(TriggerType.AgentInitiated, loaded[0].TriggerType);

        // Records without a session identifier read back null (line A / scheduled / manual snapshots)
        await repo.UpsertSnapshotAsync(new SnapshotRecord { Volumes = ["C:\\"], TriggerType = TriggerType.Scheduled });
        var all = await repo.ListSnapshotsAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.SessionId is null);
    }

    [Fact]
    public async Task EnsureCreated_MigratesLegacyDatabaseWithoutSessionIdColumn()
    {
        // Simulate a legacy database: manually create a snapshots table without the session_id column
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE snapshots (
                    id TEXT PRIMARY KEY, created_at TEXT NOT NULL, trigger_type TEXT NOT NULL,
                    trigger_detail TEXT, label TEXT, volumes_json TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL DEFAULT 0, shadow_id TEXT, device_object TEXT,
                    integrity_hash TEXT, is_pinned INTEGER NOT NULL DEFAULT 0,
                    offsite_status TEXT NOT NULL DEFAULT 'NotSynced', offsite_done_at TEXT, notes TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // EnsureCreated at startup should add the session_id column without affecting the existing table
        await DatabaseInitializer.EnsureCreatedAsync(_dbPath);

        var repo = new SqliteRepository(_dbPath);
        await repo.UpsertSnapshotAsync(new SnapshotRecord
        {
            Volumes = ["C:\\"], TriggerType = TriggerType.AgentInitiated, SessionId = "migrated-session"
        });
        var loaded = await repo.ListSnapshotsAsync();
        Assert.Single(loaded);
        Assert.Equal("migrated-session", loaded[0].SessionId);
    }
}
