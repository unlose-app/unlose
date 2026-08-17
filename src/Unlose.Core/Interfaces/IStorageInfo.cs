namespace Unlose.Core.Interfaces;

/// <summary>
/// Read-only storage status probe interface (testability refactor ARCH-testability).
/// Exposes only whether protection is suspended due to low disk space, for upper layers
/// such as SnapshotScheduler to consume, allowing a mock to be injected without starting
/// the real StorageGuard BackgroundService.
/// </summary>
public interface IStorageInfo
{
    /// <summary>Whether protection is suspended because disk space is below the threshold (automatic snapshots are skipped while suspended)</summary>
    bool IsSuspended { get; }
}
