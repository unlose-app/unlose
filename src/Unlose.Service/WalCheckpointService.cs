using Unlose.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>
/// Periodic WAL checkpoint background service.
///
/// Background: DatabaseInitializer enabled WAL mode + wal_autocheckpoint=1000 (~4MB
/// passive checkpoint), but passive checkpoints only fire on write-transaction commits;
/// if the service stays read-only for a long time (e.g. no snapshot activity at night),
/// the WAL can still pile up.
/// This service proactively runs PRAGMA wal_checkpoint(TRUNCATE) every 5 minutes to
/// fully truncate the WAL file, ensuring external diagnostic tools (sqlite3 CLI,
/// backups) read a consistent view of the database and avoiding spurious
/// "database disk image is malformed" artifacts.
/// </summary>
public class WalCheckpointService : BackgroundService
{
    private readonly ILogger<WalCheckpointService> _logger;
    private readonly SqliteRepository _repo;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public WalCheckpointService(ILogger<WalCheckpointService> logger, SqliteRepository repo)
    {
        _logger = logger;
        _repo = repo;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WalCheckpointService started: checkpoint every {Min} min", Interval.TotalMinutes);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                try
                {
                    await _repo.CheckpointAsync(stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A failed checkpoint is not fatal: the next tick retries; log for troubleshooting
                    _logger.LogWarning(ex, "WAL checkpoint failed (will retry next tick)");
                }
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) { /* normal stop */ }
        _logger.LogInformation("WalCheckpointService stopped");
    }
}
