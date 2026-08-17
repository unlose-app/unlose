using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Unlose.Service;

/// <summary>
/// BUG-004 fix: implements IHostedService, moving disk loading into StartAsync so it
/// runs asynchronously and no longer blocks the DI construction phase.
/// BUG-007 fix: SaveToDisk performs file I/O outside the lock, avoiding blocking all
/// read operations during the lock.
/// BUG-005 fix: on VSS delete failure the in-memory record is kept, keeping memory/VSS state consistent.
/// </summary>
public class RestoreService : ISnapshotService, IHostedService
{
    private readonly ILogger<RestoreService> _logger;
    private readonly IVssGateway _vss;
    private readonly List<SnapshotRecord> _snapshots = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _storePath;

    public RestoreService(ILogger<RestoreService> logger, IVssGateway vss)
    {
        _logger = logger;
        _vss = vss;
        _storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "unlose", "snapshots.json");
        // BUG-004 fix: removed the synchronous LoadFromDisk() call from the constructor
    }

    // BUG-004 fix: load the snapshot catalog asynchronously in IHostedService.StartAsync
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadFromDiskAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LoadFromDiskAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var json = await File.ReadAllTextAsync(_storePath, System.Text.Encoding.UTF8, ct);
            var loaded = JsonSerializer.Deserialize<List<SnapshotRecord>>(json);
            if (loaded is not null)
            {
                await _lock.WaitAsync(ct);
                try { _snapshots.AddRange(loaded); }
                finally { _lock.Release(); }
            }
            _logger.LogInformation("Loaded {Count} snapshots from {Path}", _snapshots.Count, _storePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load snapshot catalog from {Path}", _storePath);
        }
    }

    private async Task SaveToDiskAsync()
    {
        // BUG-007 fix: perform file I/O outside the lock (copy the snapshot list, release the lock, then write the file)
        List<SnapshotRecord> snapshot;
        await _lock.WaitAsync();
        try { snapshot = _snapshots.ToList(); }
        finally { _lock.Release(); }

        try
        {
            var dir = Path.GetDirectoryName(_storePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storePath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save snapshot catalog to {Path}", _storePath);
        }
    }

    public async Task<SnapshotRecord> CreateSnapshotAsync(string volumePath, CancellationToken ct = default)
    {
        var record = await _vss.CreateShadowCopyAsync(volumePath, ct);
        await _lock.WaitAsync(ct);
        try { _snapshots.Add(record); }
        finally { _lock.Release(); }
        await SaveToDiskAsync(); // BUG-007 fix: write the file outside the lock
        return record;
    }

    public async Task<IReadOnlyList<SnapshotRecord>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return _snapshots.ToList().AsReadOnly(); }
        finally { _lock.Release(); }
    }

    public async Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken ct = default)
    {
        SnapshotRecord? record;
        await _lock.WaitAsync(ct);
        try { record = _snapshots.FirstOrDefault(s => s.Id == snapshotId); }
        finally { _lock.Release(); }

        if (record is null) return;
        if (string.IsNullOrWhiteSpace(record.ShadowId)) return;

        // BUG-005 fix: call VSS delete first; only remove from memory on success — keep the record on failure for consistent state
        await _vss.DeleteShadowCopyAsync(record.ShadowId, ct);

        await _lock.WaitAsync(ct);
        try { _snapshots.Remove(record); }
        finally { _lock.Release(); }

        await SaveToDiskAsync(); // BUG-007 fix: write the file outside the lock
        _logger.LogInformation("Snapshot {Id} deleted.", snapshotId);
    }

    public async Task<bool> RestoreSnapshotAsync(Guid snapshotId, string? targetPath, CancellationToken ct = default)
    {
        SnapshotRecord? record;
        await _lock.WaitAsync(ct);
        try { record = _snapshots.FirstOrDefault(s => s.Id == snapshotId); }
        finally { _lock.Release(); }

        if (record is null)
        {
            _logger.LogWarning("Snapshot {Id} not found.", snapshotId);
            return false;
        }
        if (string.IsNullOrWhiteSpace(record.ShadowId))
            return false;

        // When targetPath is given, restore into that directory; otherwise roll back the original volume
        var target = !string.IsNullOrWhiteSpace(targetPath) ? targetPath : record.VolumePath;
        return await _vss.RestoreShadowCopyAsync(record.ShadowId, target, ct);
    }

    /// <summary>Mount the snapshot's shadow copy as a browsable directory (backs the MOUNT_SNAPSHOT command)</summary>
    public async Task<string?> MountSnapshotAsync(Guid snapshotId, CancellationToken ct = default)
    {
        SnapshotRecord? record;
        await _lock.WaitAsync(ct);
        try { record = _snapshots.FirstOrDefault(s => s.Id == snapshotId); }
        finally { _lock.Release(); }

        if (record is null || string.IsNullOrWhiteSpace(record.ShadowId))
            return null;

        return await _vss.MountShadowCopyAsync(record.ShadowId, ct);
    }

    /// <summary>Pick-restore specific relative-path files/directories from a snapshot (backs the RESTORE_FILES command)</summary>
    public async Task<IReadOnlyList<string>?> RestoreFilesAsync(
        Guid snapshotId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default)
    {
        SnapshotRecord? record;
        await _lock.WaitAsync(ct);
        try { record = _snapshots.FirstOrDefault(s => s.Id == snapshotId); }
        finally { _lock.Release(); }

        if (record is null || string.IsNullOrWhiteSpace(record.ShadowId))
            return null;

        return await _vss.RestoreFilesFromShadowAsync(record.ShadowId, relativePaths, targetPath, ct);
    }
}
