using Unlose.Core.Config;
using Unlose.Core.Data;
using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>
/// Tiered snapshot retention policy engine, triggered by SnapshotCreatedEvent.
///
/// Retention rules:
///   - Last 24h: keep at most Retention24hCount snapshots (UnloseConfig.Snapshot, default 30)
///   - 24h ~ 7d: keep the earliest + latest 2 per day (cross-day rollback can return
///     to the "that morning" good state)
///   - 7d ~ 30d: keep the newest 1 per week
///   - Older than 30d: delete all
///   - Snapshots with is_pinned = true are unaffected
/// Purge results are persisted to monitor_events (SnapshotPurged), so users can see
/// the cleanup actions and reasons in the event log.
/// </summary>
public class RetentionPolicyEngine
{
    private readonly ILogger<RetentionPolicyEngine> _logger;
    private readonly SqliteRepository _repo;
    private readonly IVssGateway _vss;
    private readonly UnloseConfig _config;

    public RetentionPolicyEngine(
        ILogger<RetentionPolicyEngine> logger,
        SqliteRepository repo,
        IVssGateway vss,
        UnloseConfig config)
    {
        _logger = logger;
        _repo = repo;
        _vss = vss;
        _config = config;
    }

    /// <summary>Called once after each SnapshotCreatedEvent.</summary>
    public async Task EnforceAsync(CancellationToken ct = default)
    {
        var all = await _repo.ListSnapshotsAsync(ct: ct);
        if (all.Count == 0) return;

        var now = DateTime.UtcNow;
        var toDelete = new List<SnapshotRecord>();

        // CreatedAt sources are mixed: in-memory new records are UTC, while values read
        // back from SQLite are converted to local time by DateTime.Parse.
        // DateTime subtraction ignores Kind (ticks are subtracted directly); mixing them
        // skews the age by a timezone offset.
        // Unified convention: age comparisons always use UTC; zone ② "per day" groups by
        // local date (the "day" a user perceives is local).
        var eligible = all.Where(s => !s.IsPinned)
            .Select(s => (Record: s, Utc: s.CreatedAt.ToUniversalTime()))
            .OrderByDescending(x => x.Utc)
            .ToList();

        // ① Last-24h zone: excess snapshots beyond Retention24hCount (lower bound of 1, so a config of 0 can't wipe everything)
        var maxRecent24h = Math.Max(1, _config.Snapshot.Retention24hCount);
        var recent24h = eligible.Where(x => (now - x.Utc).TotalHours <= 24).ToList();
        var purge24h = recent24h.Count > maxRecent24h
            ? recent24h.Skip(maxRecent24h).Select(x => x.Record).ToList() : [];
        toDelete.AddRange(purge24h);

        // ② 24h ~ 7d zone: keep the earliest + latest 2 per day (cross-day rollback: the morning good state won't be displaced by an evening bad state)
        var range7d = eligible
            .Where(x => (now - x.Utc).TotalHours > 24 && (now - x.Utc).TotalDays <= 7)
            .GroupBy(x => x.Utc.ToLocalTime().Date)
            .Select(g => g.OrderByDescending(x => x.Utc).ToList())
            .SelectMany(g => g.Count <= 2
                ? Enumerable.Empty<SnapshotRecord>()
                : g.Skip(1).Take(g.Count - 2).Select(x => x.Record))  // drop the head (newest) and tail (earliest); delete the ones in between
            .ToList();
        toDelete.AddRange(range7d);

        // ③ 7d ~ 30d zone: keep only the newest 1 per week
        var range30d = eligible
            .Where(x => (now - x.Utc).TotalDays > 7 && (now - x.Utc).TotalDays <= 30)
            .GroupBy(x => IsoWeekNumber(x.Utc))
            .Select(g => g.OrderByDescending(x => x.Utc).Skip(1).Select(x => x.Record))
            .SelectMany(x => x)
            .ToList();
        toDelete.AddRange(range30d);

        // ④ Older than 30d: delete all
        var old = eligible.Where(x => (now - x.Utc).TotalDays > 30).Select(x => x.Record).ToList();
        toDelete.AddRange(old);

        // Deduplicate and exclude pinned snapshots (defensive double filter)
        var distinct = toDelete.DistinctBy(s => s.Id).Where(s => !s.IsPinned).ToList();
        _logger.LogInformation("RetentionPolicyEngine: purging {N} snapshots", distinct.Count);

        foreach (var snap in distinct)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (!string.IsNullOrEmpty(snap.ShadowId))
                    await _vss.DeleteShadowCopyAsync(snap.ShadowId, ct);
                await _repo.DeleteSnapshotAsync(snap.Id, ct);
                _logger.LogDebug("Deleted snapshot {Id} ({Trigger}, {Age:N1}d)",
                    snap.Id, snap.TriggerType, (now - snap.CreatedAt.ToUniversalTime()).TotalDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to delete snapshot {Id}", snap.Id);
            }
        }

        // Purge visibility (P1-5): persist purges to monitor_events so users see "purged N, and why" in the event log
        if (distinct.Count > 0)
            await TryInsertPurgedEventAsync(distinct.Count, purge24h.Count, range7d.Count, range30d.Count, old.Count, ct);
    }

    private async Task TryInsertPurgedEventAsync(int total, int n24h, int n7d, int n30d, int nOld, CancellationToken ct)
    {
        try
        {
            await _repo.InsertMonitorEventAsync(new MonitorEventRecord
            {
                EventType = "SnapshotPurged",
                Severity = DangerSeverity.Info,
                ProcessName = "service",
                Pid = 0,
                Description = $"保留策略清理 {total} 个快照（超出24h保留数 {n24h} / 按天收敛 {n7d} / 按周收敛 {n30d} / 超过30天 {nOld}）"
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist SnapshotPurged monitor event");
        }
    }

    // ISO-8601 week number (simplified; groups by week-of-year)
    private static int IsoWeekNumber(DateTime dt) =>
        System.Globalization.ISOWeek.GetWeekOfYear(dt);

    // Startup reconciliation: remove DB records whose VSS shadow copy no longer exists.
    // Throttled entry for read paths (LIST_SNAPSHOTS): opening the snapshot library or restore
    // wizard reconciles at most once per 5 minutes, so the list never shows records whose
    // underlying shadow was already evicted by Windows.
    private DateTime _lastReconcileUtc = DateTime.MinValue;
    private int _reconcileRunning;
    private static readonly TimeSpan ReconcileMinInterval = TimeSpan.FromMinutes(5);

    public async Task ReconcileOrphansThrottledAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastReconcileUtc < ReconcileMinInterval) return;
        if (Interlocked.Exchange(ref _reconcileRunning, 1) != 0) return;
        try
        {
            _lastReconcileUtc = DateTime.UtcNow;
            await ReconcileOrphansAsync(ct);
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileRunning, 0);
        }
    }

    /// <summary>
    /// Reconciliation: remove DB records whose VSS shadow copy no longer exists.
    /// Windows silently evicts old shadow copies when shadow storage fills up, which would otherwise
    /// leave records that can never be mounted or restored ("mount failed" in the restore wizard).
    /// Safety: only runs when the WMI shadow query succeeded — a failed query skips reconciliation
    /// entirely rather than risking mass deletion.
    /// </summary>
    public async Task ReconcileOrphansAsync(CancellationToken ct = default)
    {
        IReadOnlyList<VssShadowInfo> shadows;
        try
        {
            shadows = await _vss.ListShadowCopiesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReconcileOrphans: shadow query failed, skipping reconciliation");
            return;
        }

        static string Norm(string id) => id.Trim('{', '}');
        var alive = shadows.Select(s => Norm(s.ShadowId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = await _repo.ListSnapshotsAsync(ct: ct);
        var orphans = all
            .Where(s => !string.IsNullOrEmpty(s.ShadowId) && !alive.Contains(Norm(s.ShadowId)))
            .ToList();
        if (orphans.Count == 0) return;

        var removed = 0;
        foreach (var snap in orphans)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await _repo.DeleteSnapshotAsync(snap.Id, ct);
                removed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ReconcileOrphans: failed to delete record {Id}", snap.Id);
            }
        }
        if (removed == 0) return;
        _logger.LogInformation("ReconcileOrphans: removed {N} snapshot records whose shadow copy no longer exists", removed);

        // Visibility, same pattern as SnapshotPurged: persist to monitor_events so the cleanup shows in the event log
        try
        {
            await _repo.InsertMonitorEventAsync(new MonitorEventRecord
            {
                EventType = "SnapshotReconciled",
                Severity = DangerSeverity.Info,
                ProcessName = "service",
                Pid = 0,
                Description = $"启动对账：移除 {removed} 条底层卷影已被系统清理的快照记录"
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist SnapshotReconciled monitor event");
        }
    }
}
