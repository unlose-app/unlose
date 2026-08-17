using Unlose.Core.Models;

namespace Unlose.Core.Interfaces;

/// <summary>
/// VSS shadow copy gateway interface (testability refactor ARCH-testability).
/// Abstracts WMI Win32_ShadowCopy create/delete/list and robocopy-based restore,
/// so upper layers (SnapshotManager / RestoreService / RetentionPolicyEngine) can inject mocks.
/// </summary>
public interface IVssGateway
{
    /// <summary>Creates a VSS shadow copy for the specified volume</summary>
    Task<SnapshotRecord> CreateShadowCopyAsync(string volumePath, CancellationToken ct = default);

    /// <summary>Deletes the specified shadow copy (silently succeeds; if it no longer exists no exception is thrown — the caller handles that)</summary>
    Task DeleteShadowCopyAsync(string shadowId, CancellationToken ct = default);

    /// <summary>Restores the contents of the specified snapshot to the target volume via robocopy</summary>
    Task<bool> RestoreShadowCopyAsync(string shadowId, string targetVolume, CancellationToken ct = default);

    /// <summary>Lists all VSS snapshots currently on the system (newest first by creation time)</summary>
    Task<IReadOnlyList<VssShadowInfo>> ListShadowCopiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Mounts the specified shadow copy as a browsable symlink directory (backs the MOUNT_SNAPSHOT command, letting the immersive restore page enumerate files inside a snapshot).
    /// Returns the symlink path; null if the shadow copy does not exist.
    /// The link persists under %ProgramData%\unlose\mounts\, named and reused by ShadowId.
    /// </summary>
    Task<string?> MountShadowCopyAsync(string shadowId, CancellationToken ct = default);

    /// <summary>
    /// Selectively restores the files/directories at the given relative paths from a shadow copy to the target directory (backs the RESTORE_FILES command).
    /// Returns the list of relative paths that failed to restore (empty = all succeeded); null if the shadow copy does not exist.
    /// relativePaths must be relative to the mount root (the implementation includes path-traversal protection).
    /// </summary>
    Task<IReadOnlyList<string>?> RestoreFilesFromShadowAsync(
        string shadowId, IReadOnlyList<string> relativePaths, string targetPath, CancellationToken ct = default);
}
