using Unlose.Core.Config;
using Unlose.Core.Data;
using Unlose.Core.Enums;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>Snapshot manager: create/delete/pin, event publishing, DB persistence, exponential backoff retries</summary>
public class SnapshotManager : ISnapshotService
{
    private readonly ILogger<SnapshotManager> _logger;
    private readonly IVssGateway _vss;
    private readonly SqliteRepository _repo;
    private readonly EventBus _bus;
    private readonly UnloseConfig _config;

    private const int MaxRetries = 3;

    /// <summary>Maximum time to wait for the lock while VSS is busy (a volume snapshot normally completes in 1-3 seconds)</summary>
    private static readonly TimeSpan VssBusyWait = TimeSpan.FromSeconds(15);

    /// <summary>Global mutex for VSS operations; prevents concurrent Win32_ShadowCopy.Create calls that cause returnCode=9</summary>
    private readonly SemaphoreSlim _vssMutex = new(1, 1);

    public SnapshotManager(
        ILogger<SnapshotManager> logger,
        IVssGateway vss,
        SqliteRepository repo,
        EventBus bus,
        UnloseConfig config)
    {
        _logger = logger;
        _vss = vss;
        _repo = repo;
        _bus = bus;
        _config = config;
    }

    /// <summary>Create a snapshot (exponential backoff, up to 3 retries; publishes SnapshotCreatedEvent on success, SnapshotFailedEvent on failure)</summary>
    public async Task<SnapshotRecord?> CreateAsync(
        TriggerType trigger,
        string? triggerDetail = null,
        string? label = null,
        string[]? volumes = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var targetVolumes = volumes ?? _config.Snapshot.Volumes;

        // If VSS is busy (another snapshot is being created), wait briefly instead of
        // failing immediately: the AgentPreSession hook and MCP session initialization
        // were observed to fire concurrently, and WaitAsync(0) would fail the latter outright.
        var waited = false;
        if (!await _vssMutex.WaitAsync(0, ct))
        {
            waited = true;
            if (!await _vssMutex.WaitAsync(VssBusyWait, ct))
            {
                _logger.LogWarning("Snapshot skipped (VSS busy timeout {Timeout}s): trigger={Trigger} detail={Detail}",
                    VssBusyWait.TotalSeconds, trigger, triggerDetail);
                return null;
            }
        }

        int attempt = 0;
        try
        {
        // Having queued for the lock means a snapshot was just being created: if it already
        // completed (within 60s), reuse it to avoid duplicate snapshots under races.
        // Inside the try: the finally releases _vssMutex on early return.
        if (waited)
        {
            SnapshotRecord? recent = null;
            try { recent = await FindRecentCreatedAsync(TimeSpan.FromSeconds(60), ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Recent-snapshot coalesce check failed, proceeding to create"); }
            if (recent is not null)
            {
                _logger.LogInformation("Snapshot coalesced: reusing {Id} (created {CreatedAt}, trigger={RecentTrigger}) for trigger={Trigger}",
                    recent.Id, recent.CreatedAt, recent.TriggerType, trigger);
                return recent;
            }
        }

        while (attempt < MaxRetries)
        {
            var createdThisAttempt = new List<SnapshotRecord>();
            try
            {
                _logger.LogInformation("Creating snapshot attempt {Attempt}/{Max}, trigger={Trigger}", attempt + 1, MaxRetries, trigger);

                // Create a VSS shadow copy per volume (one record per volume; avoids leaks from multi-volume overwrite)
                foreach (var vol in targetVolumes)
                {
                    var record = await _vss.CreateShadowCopyAsync(vol, ct);

                    // VssAdapter may return a Failed status instead of throwing; normalize to the exception path to trigger retry
                    if (record.Status == SnapshotStatus.Failed)
                        throw new InvalidOperationException($"VSS snapshot failed for volume {vol}");

                    // Fill in metadata (each record corresponds to one volume)
                    record.TriggerType = trigger;
                    record.TriggerDetail = triggerDetail;
                    record.Label = label;
                    record.SessionId = sessionId;
                    record.Volumes = [vol];

                    await _repo.UpsertSnapshotAsync(record, ct);
                    _bus.Publish(new SnapshotCreatedEvent(record));
                    createdThisAttempt.Add(record);
                }

                if (createdThisAttempt.Count == 0)
                    throw new InvalidOperationException("No volumes to snapshot");

                var first = createdThisAttempt[0];
                _logger.LogInformation("Snapshot created: {Count} volume(s), first={Id}", createdThisAttempt.Count, first.Id);
                return first;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // On failure, roll back snapshots created in this attempt to avoid VSS resource leaks from multi-volume partial success
                foreach (var created in createdThisAttempt)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(created.ShadowId))
                            await _vss.DeleteShadowCopyAsync(created.ShadowId, ct);
                        await _repo.DeleteSnapshotAsync(created.Id, ct);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, "Cleanup failed for partial snapshot {Id}", created.Id);
                    }
                }

                attempt++;
                _logger.LogWarning(ex, "Snapshot creation failed (attempt {Attempt})", attempt);
                _bus.Publish(new SnapshotFailedEvent(trigger, ex.Message, attempt));

                if (attempt >= MaxRetries) break;

                // Exponential backoff: 1s / 2s / 4s
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct);
            }
        }

            _logger.LogError("Snapshot creation failed after {MaxRetries} attempts", MaxRetries);
            return null;
        }
        finally
        {
            _vssMutex.Release();
        }
    }

    /// <summary>Find the newest snapshot created successfully within the window (for race coalescing after VSS-busy queuing); null if none.</summary>
    private async Task<SnapshotRecord?> FindRecentCreatedAsync(TimeSpan window, CancellationToken ct)
    {
        var all = await _repo.ListSnapshotsAsync(null, ct);
        var cutoff = DateTime.UtcNow - window;
        var recent = all.FirstOrDefault(s => s.CreatedAt.ToUniversalTime() >= cutoff);
        return recent;
    }

    /// <summary>Delete a snapshot (pinned snapshots are refused)</summary>
    public async Task<bool> DeleteAsync(Guid snapshotId, CancellationToken ct = default)
    {
        var record = await _repo.GetSnapshotAsync(snapshotId, ct);
        if (record == null)
        {
            _logger.LogWarning("DeleteAsync: snapshot {Id} not found", snapshotId);
            return false;
        }

        if (record.IsPinned)
        {
            _logger.LogWarning("DeleteAsync: snapshot {Id} is pinned, refusing delete", snapshotId);
            return false;
        }

        if (!string.IsNullOrEmpty(record.ShadowId))
        {
            try { await _vss.DeleteShadowCopyAsync(record.ShadowId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "VSS delete failed for {ShadowId}", record.ShadowId); }
        }

        await _repo.DeleteSnapshotAsync(snapshotId, ct);
        return true;
    }

    /// <summary>Set the pinned state of a snapshot</summary>
    public async Task SetPinnedAsync(Guid snapshotId, bool pinned, CancellationToken ct = default)
    {
        var record = await _repo.GetSnapshotAsync(snapshotId, ct);
        if (record == null) return;
        record.IsPinned = pinned;
        await _repo.UpsertSnapshotAsync(record, ct);
    }

    public Task<List<SnapshotRecord>> ListAsync(TriggerType? filter = null, CancellationToken ct = default)
        => _repo.ListSnapshotsAsync(filter, ct);

    public async Task<SnapshotRecord> CreateSnapshotAsync(string volumePath, CancellationToken ct = default)
        => await CreateAsync(TriggerType.Manual, volumes: [volumePath], ct: ct)
            ?? throw new InvalidOperationException("Snapshot creation failed.");

    public async Task<IReadOnlyList<SnapshotRecord>> ListSnapshotsAsync(CancellationToken ct = default)
        => await ListAsync(ct: ct);

    public async Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken ct = default)
    {
        await DeleteAsync(snapshotId, ct);
    }

    public async Task<bool> RestoreSnapshotAsync(Guid snapshotId, string? targetPath, CancellationToken ct = default)
    {
        var record = await _repo.GetSnapshotAsync(snapshotId, ct);
        if (record is null || string.IsNullOrWhiteSpace(record.ShadowId))
            return false;

        // When targetPath is given, restore into that directory; otherwise roll back the original volume ("in-place force overwrite" semantics)
        var target = !string.IsNullOrWhiteSpace(targetPath)
            ? targetPath
            : record.Volumes.FirstOrDefault() ?? record.VolumePath;
        if (string.IsNullOrWhiteSpace(target))
            return false;

        return await _vss.RestoreShadowCopyAsync(record.ShadowId, target, ct);
    }

    /// <summary>Mount the snapshot's shadow copy as a browsable directory (backs the MOUNT_SNAPSHOT command)</summary>
    public async Task<string?> MountSnapshotAsync(Guid snapshotId, CancellationToken ct = default)
    {
        var record = await _repo.GetSnapshotAsync(snapshotId, ct);
        if (record is null || string.IsNullOrWhiteSpace(record.ShadowId))
            return null;

        return await _vss.MountShadowCopyAsync(record.ShadowId, ct);
    }

    /// <summary>Pick-restore specific relative-path files/directories from a snapshot (backs the RESTORE_FILES command)</summary>
    public async Task<IReadOnlyList<string>?> RestoreFilesAsync(
        Guid snapshotId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default)
    {
        var record = await _repo.GetSnapshotAsync(snapshotId, ct);
        if (record is null || string.IsNullOrWhiteSpace(record.ShadowId))
            return null;

        return await _vss.RestoreFilesFromShadowAsync(record.ShadowId, relativePaths, targetPath, ct);
    }
}
