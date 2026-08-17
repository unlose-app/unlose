using Unlose.Core.Data;
using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Ipc;
using Unlose.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>
/// Main worker: starts PipeServer + Heartbeat + the event consumption loop.
/// SnapshotScheduler / StorageGuard / AgentSessionManager /
/// SystemRestoreService / ProtectionPauseManager are hosted as independent BackgroundServices.
/// </summary>
public class UnloseWorker : BackgroundService
{
    private readonly ILogger<UnloseWorker> _logger;
    private readonly IPipeServer _pipeServer;
    private readonly HeartbeatService _heartbeat;
    private readonly EventBus _bus;
    private readonly SnapshotManager _snapshotManager;
    private readonly RetentionPolicyEngine _retentionEngine;
    private readonly SqliteRepository _repository;

    public UnloseWorker(
        ILogger<UnloseWorker> logger,
        IPipeServer pipeServer,
        HeartbeatService heartbeat,
        EventBus bus,
        SnapshotManager snapshotManager,
        RetentionPolicyEngine retentionEngine,
        SqliteRepository repository)
    {
        _logger = logger;
        _pipeServer = pipeServer;
        _heartbeat = heartbeat;
        _bus = bus;
        _snapshotManager = snapshotManager;
        _retentionEngine = retentionEngine;
        _repository = repository;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Unlose Protection Service starting...");
        // Startup reconciliation: drop snapshot records whose VSS shadow was silently evicted by Windows
        // (fire-and-forget; must never delay or break service startup)
        _ = Task.Run(() => _retentionEngine.ReconcileOrphansAsync(stoppingToken), stoppingToken);
        // Startup cleanup: drop stale mount symlinks from a previous run. A restart invalidates the
        // GLOBALROOT device paths they point to, so they are unusable and would otherwise accumulate
        // (one link per previously mounted shadow, never cleaned). Runs before the pipe server accepts
        // connections so no in-flight mount can be disturbed.
        CleanupStaleMounts();
        try
        {
            await Task.WhenAll(
                _pipeServer.StartAsync(stoppingToken),
                _heartbeat.RunAsync(stoppingToken),
                RunEventLoopAsync(stoppingToken)
            );
        }
        catch (OperationCanceledException) { /* normal stop */ }
        finally
        {
            await _pipeServer.StopAsync();
            _logger.LogInformation("Unlose Protection Service stopped.");
        }
    }

    private async Task RunEventLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Event loop started");
        await foreach (var evt in _bus.Reader.ReadAllAsync(ct))
        {
            try { await DispatchEventAsync(evt, ct); }
            catch (Exception ex) { _logger.LogError(ex, "Event dispatch error for {T}", evt.GetType().Name); }
        }
    }

    /// <summary>
    /// Removes stale mount symlinks left under %ProgramData%\unlose\mounts\ by a previous process run.
    /// Each MOUNT_SNAPSHOT creates one directory symlink per shadow (reused on re-mount); after a service
    /// restart the GLOBALROOT device paths they point to are no longer valid, and nothing else ever removes
    /// them, so they would accumulate forever. Deleting them at startup is safe: no mount can be in flight
    /// before the pipe server accepts connections, and the immersive restore page re-mounts on demand.
    /// </summary>
    private void CleanupStaleMounts()
    {
        try
        {
            var mountsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "unlose", "mounts");
            if (!Directory.Exists(mountsDir)) return;

            var removed = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(mountsDir))
            {
                try
                {
                    // Entries are directory symlinks (reparse points): File.Delete cannot remove a directory,
                    // so branch on the attribute. Directory.Delete(recursive:false) removes the link itself
                    // without touching the shadow target; plain files (should not occur) are deleted directly.
                    var attr = File.GetAttributes(entry);
                    if ((attr & FileAttributes.Directory) != 0)
                        Directory.Delete(entry, recursive: false);
                    else
                        File.Delete(entry);
                    removed++;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove stale mount link {Path}", entry); }
            }
            if (removed > 0)
                _logger.LogInformation("Cleaned {N} stale mount link(s)", removed);
        }
        catch (Exception ex)
        {
            // Startup must never break because of link cleanup; log and continue.
            _logger.LogWarning(ex, "Stale mount cleanup failed");
        }
    }

    private async Task DispatchEventAsync(IUnloseEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case AgentSessionStartedEvent session:
                await _pipeServer.BroadcastAsync(new AgentSessionStartedNotification(
                    session.Session.Id, session.Session.ProcessName,
                    session.SnapshotCreated, session.IsUnprotected), ct);
                break;

            case AgentSessionEndedEvent ended:
                await _pipeServer.BroadcastAsync(new AgentSessionEndedNotification(
                    ended.Session.Id, ended.Session.ProcessName), ct);
                break;

            case SnapshotCreatedEvent created:
                await _pipeServer.BroadcastAsync(new SnapshotCreatedNotification(created.Snapshot), ct);
                await TryInsertMonitorEventAsync("SnapshotCreated", DangerSeverity.Info,
                    $"快照已创建 ({DescribeTrigger(created.Snapshot)}): {string.Join(", ", created.Snapshot.Volumes)}", ct);
                _ = Task.Run(() => _retentionEngine.EnforceAsync(ct), ct);
                break;

            case SnapshotFailedEvent failed:
                await _pipeServer.BroadcastAsync(new SnapshotFailedNotification(
                    failed.TriggerType, failed.Reason, failed.RetryCount), ct);
                await TryInsertMonitorEventAsync("SnapshotFailed", DangerSeverity.High,
                    $"快照创建失败 ({TriggerName(failed.TriggerType)}): {failed.Reason}（重试 {failed.RetryCount} 次）", ct);
                break;

            case StorageLowEvent low:
                await _pipeServer.BroadcastAsync(new StorageLowNotification(
                    low.Volume, low.FreeGb, low.ThresholdGb), ct);
                await TryInsertMonitorEventAsync("StorageLow", DangerSeverity.High,
                    $"存储空间不足：{low.Volume} 剩余 {low.FreeGb:F1}GB，低于阈值 {low.ThresholdGb:F1}GB", ct);
                break;

            case ProtectionStateChangedEvent stateChange:
                await _pipeServer.BroadcastAsync(new ProtectionStateChangedNotification(
                    stateChange.IsPaused, stateChange.ResumesAt), ct);
                await TryInsertMonitorEventAsync("ProtectionStateChanged", DangerSeverity.Info,
                    DescribeProtectionState(stateChange), ct);
                break;

            default:
                _logger.LogDebug("Unhandled event: {T}", evt.GetType().Name);
                break;
        }
    }

    // Persist events to monitor_events (contract with the UI: EventType is fixed to the four strings above; Severity is Info/High only).
    // AgentSessionStarted/Ended are already written to audit_log by AgentSessionManager; not duplicated here.
    // Persistence failures are only logged and never affect the main event dispatch flow.
    private async Task TryInsertMonitorEventAsync(string eventType, DangerSeverity severity, string description, CancellationToken ct)
    {
        try
        {
            await _repository.InsertMonitorEventAsync(new MonitorEventRecord
            {
                EventType = eventType,
                Severity = severity,
                ProcessName = "service",
                Pid = 0,
                Description = description
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist monitor event {EventType}", eventType);
        }
    }

    // Event description for protection state changes: includes pause/resume and resume time (ResumesAt is null for UntilReboot)
    private static string DescribeProtectionState(ProtectionStateChangedEvent e)
    {
        if (!e.IsPaused) return "保护已恢复";
        return e.ResumesAt.HasValue
            ? $"保护已暂停，预计 {e.ResumesAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} 恢复"
            : "保护已暂停（直到重启）";
    }

    // Display name of trigger types in monitor event Description (Chinese names, not English enum names, to match the description style)
    private static string TriggerName(TriggerType t) => t switch
    {
        TriggerType.Scheduled => "定时快照",
        TriggerType.AgentPreSession => "Agent启动前",
        TriggerType.Manual => "手动",
        TriggerType.PreRestore => "还原前备份",
        TriggerType.AgentInitiated => "Agent主动快照",
        _ => t.ToString()
    };

    // Trigger description for snapshot-created events: when triggered by an agent, merge
    // the caller's note (label) into the display, e.g. "Agent主动快照·新会话开始";
    // when label is missing, fall back to triggerDetail
    private static string DescribeTrigger(SnapshotRecord s)
    {
        var note = s.Label ?? (s.TriggerType == TriggerType.AgentInitiated ? s.TriggerDetail : null);
        return string.IsNullOrWhiteSpace(note)
            ? TriggerName(s.TriggerType)
            : $"{TriggerName(s.TriggerType)}·{note}";
    }
}
