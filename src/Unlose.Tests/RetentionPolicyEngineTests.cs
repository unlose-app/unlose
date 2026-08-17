using Unlose.Core.Config;
using Unlose.Core.Data;
using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Unlose.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Unlose.Tests;

/// <summary>
/// RetentionPolicyEngine tiered retention policy tests (P1-5/P1-6):
/// daily tier keeps the earliest + latest 2, weekly tier keeps only the latest 1, pinned is exempt, purge emits a SnapshotPurged monitoring event.
/// </summary>
public class RetentionPolicyEngineTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public RetentionPolicyEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "unlose-retention-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "snapshots.db");
    }

    public void Dispose()
    {
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<(RetentionPolicyEngine engine, SqliteRepository repo)> BuildAsync(IVssGateway? vss = null)
    {
        await DatabaseInitializer.EnsureCreatedAsync(_dbPath);
        var repo = new SqliteRepository(_dbPath);
        var engine = new RetentionPolicyEngine(
            NullLogger<RetentionPolicyEngine>.Instance,
            repo,
            vss ?? new FakeVssGateway(),
            new UnloseConfig());
        return (engine, repo);
    }

    private static SnapshotRecord At(DateTime createdAtUtc, bool pinned = false) => new()
    {
        Volumes = ["C:\\"],
        TriggerType = TriggerType.AgentInitiated,
        CreatedAt = createdAtUtc,
        IsPinned = pinned
    };

    [Fact]
    public async Task DailyThinning_KeepsEarliestAndLatest()
    {
        var (engine, repo) = await BuildAsync();
        // 2 days ago (local day), 4 snapshots on the same day: 06:00 / 09:00 / 15:00 / 22:00
        // Constructed with local dates: the DB reads back local time, and the weekly tier groups by local date
        var day = DateTime.Now.Date.AddDays(-2);
        var s0600 = At(day.AddHours(6));
        var s0900 = At(day.AddHours(9));
        var s1500 = At(day.AddHours(15));
        var s2200 = At(day.AddHours(22));
        foreach (var s in new[] { s0600, s0900, s1500, s2200 })
            await repo.UpsertSnapshotAsync(s);

        await engine.EnforceAsync();

        var remaining = (await repo.ListSnapshotsAsync()).Select(s => s.Id).ToHashSet();
        Assert.Equal(2, remaining.Count);
        Assert.Contains(s0600.Id, remaining); // earliest kept
        Assert.Contains(s2200.Id, remaining); // latest kept
        Assert.DoesNotContain(s0900.Id, remaining);
        Assert.DoesNotContain(s1500.Id, remaining);
    }

    [Fact]
    public async Task WeeklyThinning_StillKeepsLatestOnly()
    {
        var (engine, repo) = await BuildAsync();
        // 10 days ago (local), 3 snapshots in the same ISO week
        var baseDay = DateTime.Now.Date.AddDays(-10);
        var s1 = At(baseDay.AddHours(8));
        var s2 = At(baseDay.AddHours(12));
        var s3 = At(baseDay.AddHours(20));
        foreach (var s in new[] { s1, s2, s3 })
            await repo.UpsertSnapshotAsync(s);

        await engine.EnforceAsync();

        var remaining = (await repo.ListSnapshotsAsync()).Select(s => s.Id).ToList();
        Assert.Single(remaining);
        Assert.Equal(s3.Id, remaining[0]); // weekly tier keeps only the latest
    }

    [Fact]
    public async Task Pinned_NeverPurged()
    {
        var (engine, repo) = await BuildAsync();
        var pinned = At(DateTime.UtcNow.AddDays(-40), pinned: true);
        var old = At(DateTime.UtcNow.AddDays(-40));
        await repo.UpsertSnapshotAsync(pinned);
        await repo.UpsertSnapshotAsync(old);

        await engine.EnforceAsync();

        var remaining = (await repo.ListSnapshotsAsync()).Select(s => s.Id).ToList();
        Assert.Single(remaining);
        Assert.Equal(pinned.Id, remaining[0]); // unpinned older than 30d deleted, pinned exempt
    }

    [Fact]
    public async Task Purge_WritesSnapshotPurgedMonitorEvent()
    {
        var (engine, repo) = await BuildAsync();
        var day = DateTime.Now.Date.AddDays(-2);
        foreach (var s in new[] { At(day.AddHours(6)), At(day.AddHours(9)), At(day.AddHours(22)) })
            await repo.UpsertSnapshotAsync(s);

        await engine.EnforceAsync();

        var events = await repo.ListMonitorEventsAsync(eventType: "SnapshotPurged");
        var purged = Assert.Single(events);
        Assert.Equal(DangerSeverity.Info, purged.Severity);
        Assert.Contains("按天收敛 1", purged.Description);
    }

    [Fact]
    public async Task NoPurge_NoEvent()
    {
        var (engine, repo) = await BuildAsync();
        await repo.UpsertSnapshotAsync(At(DateTime.UtcNow.AddHours(-1)));

        await engine.EnforceAsync();

        var events = await repo.ListMonitorEventsAsync(eventType: "SnapshotPurged");
        Assert.Empty(events);
    }

    [Fact]
    public async Task ReconcileOrphans_RemovesRecordsWhoseShadowIsGone()
    {
        // Startup reconciliation: Windows silently evicts old shadow copies when shadow storage
        // fills up; records pointing at gone shadows can never mount and must be removed.
        var aliveShadow = "{11111111-1111-1111-1111-111111111111}";
        var (engine, repo) = await BuildAsync(
            new FakeVssGateway(shadows: [new VssShadowInfo { ShadowId = aliveShadow }]));

        var alive = At(DateTime.UtcNow.AddHours(-2)); alive.ShadowId = aliveShadow;
        var orphan = At(DateTime.UtcNow.AddDays(-3)); orphan.ShadowId = "{22222222-2222-2222-2222-222222222222}";
        await repo.UpsertSnapshotAsync(alive);
        await repo.UpsertSnapshotAsync(orphan);

        await engine.ReconcileOrphansAsync();

        var remaining = (await repo.ListSnapshotsAsync()).Select(s => s.Id).ToHashSet();
        Assert.Contains(alive.Id, remaining);
        Assert.DoesNotContain(orphan.Id, remaining);
        var events = await repo.ListMonitorEventsAsync(eventType: "SnapshotReconciled");
        Assert.Single(events);
    }

    [Fact]
    public async Task ReconcileOrphans_ShadowQueryFailure_KeepsEverything()
    {
        // Safety guard: a failed WMI query must skip reconciliation, never mass-delete records
        var (engine, repo) = await BuildAsync(new FakeVssGateway(throwOnList: true));
        var rec = At(DateTime.UtcNow.AddHours(-2)); rec.ShadowId = "{33333333-3333-3333-3333-333333333333}";
        await repo.UpsertSnapshotAsync(rec);

        await engine.ReconcileOrphansAsync();

        var remaining = (await repo.ListSnapshotsAsync()).Select(s => s.Id).ToHashSet();
        Assert.Contains(rec.Id, remaining);
    }

    [Fact]
    public async Task ReconcileThrottled_RunsAtMostOnceWithinInterval()
    {
        // LIST_SNAPSHOTS triggers a throttled reconcile: at most one WMI sweep per 5 minutes
        var fake = new FakeVssGateway();
        var (engine, repo) = await BuildAsync(fake);
        var rec = At(DateTime.UtcNow.AddHours(-2)); rec.ShadowId = "{44444444-4444-4444-4444-444444444444}";
        await repo.UpsertSnapshotAsync(rec);

        await engine.ReconcileOrphansThrottledAsync();
        await engine.ReconcileOrphansThrottledAsync();
        await engine.ReconcileOrphansThrottledAsync();

        Assert.Equal(1, fake.ListCallCount);
    }

    private sealed class FakeVssGateway : IVssGateway
    {
        private readonly IReadOnlyList<VssShadowInfo> _shadows;
        private readonly bool _throwOnList;

        public int ListCallCount { get; private set; }

        public FakeVssGateway(IReadOnlyList<VssShadowInfo>? shadows = null, bool throwOnList = false)
        {
            _shadows = shadows ?? [];
            _throwOnList = throwOnList;
        }

        public Task<SnapshotRecord> CreateShadowCopyAsync(string volumePath, CancellationToken ct = default)
            => Task.FromResult(new SnapshotRecord { Volumes = [volumePath] });
        public Task DeleteShadowCopyAsync(string shadowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RestoreShadowCopyAsync(string shadowId, string targetVolume, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<IReadOnlyList<VssShadowInfo>> ListShadowCopiesAsync(CancellationToken ct = default)
        {
            ListCallCount++;
            return _throwOnList
                ? Task.FromException<IReadOnlyList<VssShadowInfo>>(new InvalidOperationException("WMI unavailable"))
                : Task.FromResult(_shadows);
        }
        public Task<string?> MountShadowCopyAsync(string shadowId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> RestoreFilesFromShadowAsync(
            string shadowId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>?>([]);
    }
}
