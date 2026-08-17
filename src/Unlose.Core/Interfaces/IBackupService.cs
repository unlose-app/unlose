namespace Unlose.Core.Interfaces;

public interface IBackupService
{
    Task<bool> BackupSnapshotAsync(Guid snapshotId, string destinationPath, CancellationToken ct = default);
    Task<bool> RestoreFromBackupAsync(string backupPath, string targetVolume, CancellationToken ct = default);
}
