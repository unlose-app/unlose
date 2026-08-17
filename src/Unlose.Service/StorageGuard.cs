using Unlose.Core.Config;
using Unlose.Core.Interfaces;
using Unlose.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

/// <summary>
/// Disk space monitor: suspends automatic snapshots and publishes StorageLowEvent when
/// free space drops below the threshold.
/// Implements <see cref="IStorageInfo"/> (read-only probing, injected into SnapshotScheduler)
/// and <see cref="ISuspendable"/> (can be paused/resumed by ProtectionPauseManager).
/// </summary>
public sealed class StorageGuard : BackgroundService, ISuspendable, IStorageInfo
{
    private readonly ILogger<StorageGuard> _logger;
    private readonly EventBus _bus;
    private readonly UnloseConfig _config;

    private volatile bool _isSuspended;
    private bool _wasLow;

    public bool IsSuspended => _isSuspended;

    /// <summary>
    /// The current running instance (DI singleton). CommandDispatcher reads the suspended
    /// state directly from here instead of constructor injection, appending it to STATUS responses.
    /// </summary>
    public static StorageGuard? Current { get; private set; }

    public StorageGuard(ILogger<StorageGuard> logger, EventBus bus, UnloseConfig config)
    {
        _logger = logger;
        _bus = bus;
        _config = config;
        Current = this;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StorageGuard started (threshold={Threshold}GB)", _config.Snapshot.StorageThresholdGb);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            CheckStorage();
        }
    }

    private void CheckStorage()
    {
        foreach (var vol in _config.Snapshot.Volumes)
        {
            try
            {
                var root = vol.TrimEnd('\\') + "\\";
                var drive = new DriveInfo(root);
                if (!drive.IsReady) continue;

                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var thresholdGb = _config.Snapshot.StorageThresholdGb;

                if (freeGb < thresholdGb)
                {
                    if (!_wasLow)
                    {
                        _logger.LogWarning("Storage low on {Volume}: {FreeGb:F1}GB < {Threshold}GB", vol, freeGb, thresholdGb);
                        _isSuspended = true;
                        _wasLow = true;
                        _bus.Publish(new StorageLowEvent(vol, freeGb, thresholdGb));
                    }
                }
                else
                {
                    if (_wasLow)
                    {
                        _logger.LogInformation("Storage recovered on {Volume}: {FreeGb:F1}GB", vol, freeGb);
                        _isSuspended = false;
                        _wasLow = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StorageGuard check error for {Volume}", vol);
            }
        }
    }

    void Core.Interfaces.ISuspendable.Suspend() => _isSuspended = true;
    void Core.Interfaces.ISuspendable.Resume() => _isSuspended = false;
}
