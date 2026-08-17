using Unlose.Core.Config;
using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace Unlose.Service;

/// <summary>
/// Snapshot scheduler — PeriodicTimer + catch-up creation + low-battery deferral + ISuspendable
/// </summary>
public class SnapshotScheduler : BackgroundService, ISuspendable
{
    private readonly ILogger<SnapshotScheduler> _logger;
    private readonly SnapshotManager _snapshotManager;
    private readonly IStorageInfo _storageGuard;
    private readonly UnloseConfig _config;

    private volatile bool _suspended;
    public bool IsSuspended => _suspended;

    public SnapshotScheduler(
        ILogger<SnapshotScheduler> logger,
        SnapshotManager snapshotManager,
        IStorageInfo storageGuard,
        UnloseConfig config)
    {
        _logger = logger;
        _snapshotManager = snapshotManager;
        _storageGuard = storageGuard;
        _config = config;
    }

    // ── ISuspendable ──────────────────────────────────────────────────────────
    public void Suspend() { _suspended = true;  _logger.LogInformation("SnapshotScheduler suspended"); }
    public void Resume()  { _suspended = false; _logger.LogInformation("SnapshotScheduler resumed"); }

    // ── BackgroundService ─────────────────────────────────────────────────────
    // Unified 30s tick: each round picks the scheduling mode from the current config —
    //   ScheduleTimes non-empty: fixed-time mode (default 08:00/13:00/18:00, the starts
    //   of three work periods);
    //   ScheduleTimes empty: legacy interval mode (IntervalHours, with startup catch-up
    //   semantics based on the age of the newest snapshot in the DB).
    // Config is updated in place via RELOAD_CONFIG and re-read every round; no service restart needed.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SnapshotScheduler started");

        // Startup buffer to avoid competing for I/O with system startup / service initialization
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_suspended)
                    _logger.LogDebug("SnapshotScheduler: skipped (suspended)");
                else if (_storageGuard.IsSuspended)
                    _logger.LogWarning("SnapshotScheduler: skipped (StorageGuard suspended - low disk)");
                else if (ShouldDeferForBattery())
                    _logger.LogWarning("SnapshotScheduler: deferred (battery < 20% on DC)");
                else
                {
                    var slots = ParseScheduleTimes();
                    if (slots.Count > 0)
                        await FixedTimeTickAsync(slots, stoppingToken);
                    else
                        await IntervalTickAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "SnapshotScheduler tick error"); }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        _logger.LogInformation("SnapshotScheduler stopped.");
    }

    // ── Fixed-time mode (08:00/13:00/18:00) ─────────────────────────────────────────
    private async Task FixedTimeTickAsync(IReadOnlyList<TimeSpan> slots, CancellationToken ct)
    {
        var latestLocal = (await GetLatestScheduledUtcAsync(ct))?.ToLocalTime();
        var due = GetDueSlot(DateTime.Now, latestLocal, slots);
        if (due is null) return;

        _logger.LogInformation("SnapshotScheduler: fixed-time slot due {Slot:HH:mm}, creating snapshot", due.Value);
        await TriggerScheduledSnapshotsAsync(ct);
    }

    /// <summary>
    /// Fixed-time scheduling decision (pure function for unit tests): among "today's
    /// reached slots + yesterday's latest slot", take the latest due slot after
    /// lastScheduled; returns null if none.
    /// The rule guarantees at most one catch-up tick (the latest missed slot), so even
    /// days of downtime won't cause a catch-up storm.
    /// </summary>
    internal static DateTime? GetDueSlot(DateTime nowLocal, DateTime? lastScheduledLocal, IReadOnlyList<TimeSpan> slots)
    {
        if (slots.Count == 0) return null;
        var today = nowLocal.Date;
        DateTime? due = null;

        foreach (var slot in slots)
        {
            var slotTime = today.Add(slot);
            if (slotTime <= nowLocal && (lastScheduledLocal is null || lastScheduledLocal < slotTime))
                if (due is null || slotTime > due)
                    due = slotTime;
        }

        if (due is null)
        {
            // No reached slot today (e.g. started after midnight): yesterday's latest slot counts as due if not yet covered
            var yesterdayLast = today.AddDays(-1).Add(slots.Max());
            if (yesterdayLast <= nowLocal && (lastScheduledLocal is null || lastScheduledLocal < yesterdayLast))
                due = yesterdayLast;
        }

        return due;
    }

    // ── Interval mode (legacy behavior, used when ScheduleTimes is empty) ──────────────────────────
    private async Task IntervalTickAsync(CancellationToken ct)
    {
        var intervalHours = _config.Snapshot.IntervalHours;
        if (intervalHours <= 0) return; // 0 = manual only

        var interval = TimeSpan.FromHours(intervalHours);
        var latestUtc = await GetLatestScheduledUtcAsync(ct);
        if (latestUtc is not null && DateTime.UtcNow - latestUtc.Value < interval) return;

        _logger.LogInformation(
            "SnapshotScheduler: interval {H}h elapsed (latest scheduled={Time}), creating snapshot",
            intervalHours, latestUtc);
        await TriggerScheduledSnapshotsAsync(ct);
    }

    private async Task<DateTime?> GetLatestScheduledUtcAsync(CancellationToken ct)
    {
        var scheduled = await _snapshotManager.ListAsync(TriggerType.Scheduled, ct: ct);
        return scheduled.Count == 0 ? null : scheduled.Max(s => s.CreatedAt);
    }

    private List<TimeSpan> ParseScheduleTimes()
    {
        var slots = new List<TimeSpan>();
        foreach (var raw in _config.Snapshot.ScheduleTimes ?? [])
        {
            if (TimeSpan.TryParse(raw, out var slot) && slot >= TimeSpan.Zero && slot < TimeSpan.FromDays(1))
                slots.Add(slot);
            else
                _logger.LogWarning("SnapshotScheduler: ignoring invalid ScheduleTimes entry '{Raw}'", raw);
        }
        return slots;
    }

    // ── Snapshot triggering ──────────────────────────────────────────────────────────────
    private async Task TriggerScheduledSnapshotsAsync(CancellationToken ct)
    {
        var volumes = _config.Snapshot.Volumes.Length > 0
            ? _config.Snapshot.Volumes
            : new[] { "C:\\" };

        foreach (var volume in volumes)
        {
            await CreateScheduledSnapshotAsync(volume, ct);
        }
    }

    private async Task CreateScheduledSnapshotAsync(string volume, CancellationToken ct)
    {
        try
        {
            var snap = await _snapshotManager.CreateAsync(
                TriggerType.Scheduled,
                triggerDetail: "Scheduled",
                volumes: new[] { volume },
                ct: ct);
            if (snap is not null)
                _logger.LogInformation("Scheduled snapshot created: {Id} for {Vol}", snap.Id, volume);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed scheduled snapshot for {Vol}", volume);
        }
    }

    // ── Low-battery detection ────────────────────────────────────────────────────────────
    private static bool ShouldDeferForBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return false;
            // ACLineStatus == 0: on battery; BatteryLifePercent: 255 = unknown
            bool onBattery = status.ACLineStatus == 0;
            bool lowBattery = status.BatteryLifePercent is < 20 and not 255;
            return onBattery && lowBattery;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryFullLifeTime;
        public int BatteryLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
