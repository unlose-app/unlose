namespace Unlose.Core.Interfaces;

public interface ISnapshotService
{
    Task<Models.SnapshotRecord> CreateSnapshotAsync(string volumePath, CancellationToken ct = default);
    Task<IReadOnlyList<Models.SnapshotRecord>> ListSnapshotsAsync(CancellationToken ct = default);
    Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken ct = default);

    /// <summary>Restores a snapshot. When targetPath is empty, rolls back the original volume (dangerous operation); otherwise restores to the specified directory.</summary>
    Task<bool> RestoreSnapshotAsync(Guid snapshotId, string? targetPath, CancellationToken ct = default);

    /// <summary>
    /// Selective restore: copies the files/directories at the given relative paths in the snapshot to targetPath.
    /// Returns the list of relative paths that failed (empty = all succeeded); null if the snapshot or shadow copy does not exist.
    /// </summary>
    Task<IReadOnlyList<string>?> RestoreFilesAsync(
        Guid snapshotId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default);

    /// <summary>Mounts a snapshot's shadow copy as a browsable directory and returns its root path; null if the snapshot or shadow copy does not exist</summary>
    Task<string?> MountSnapshotAsync(Guid snapshotId, CancellationToken ct = default);
}
