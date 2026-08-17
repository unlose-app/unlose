namespace Unlose.Core.Interfaces;

public interface IMonitorService
{
    event EventHandler<Models.AlertRecord> ThreatDetected;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    bool IsRunning { get; }
}
