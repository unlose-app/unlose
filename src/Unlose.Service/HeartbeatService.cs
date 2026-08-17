using Unlose.Core.Interfaces;
using Unlose.Core.Ipc;
using Microsoft.Extensions.Logging;

namespace Unlose.Service;

public class HeartbeatService
{
    private readonly ILogger<HeartbeatService> _logger;
    private readonly IPipeServer _pipeServer;
    private DateTime _lastBeat = DateTime.UtcNow;

    // CODE-009 fix: heartbeat file path; the UI reads this file to determine whether the service is alive
    private static readonly string HeartbeatFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "unlose", "heartbeat.json");

    public HeartbeatService(ILogger<HeartbeatService> logger, IPipeServer pipeServer)
    {
        _logger = logger;
        _pipeServer = pipeServer;
    }

    public DateTime LastBeat => _lastBeat;

    public async Task RunAsync(CancellationToken ct)
    {
        // Ensure the directory exists
        var dir = Path.GetDirectoryName(HeartbeatFilePath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        while (!ct.IsCancellationRequested)
        {
            _lastBeat = DateTime.UtcNow;
            _logger.LogDebug("Heartbeat at {Time}", _lastBeat);
            await WriteHeartbeatFileAsync(ct);
            await _pipeServer.BroadcastAsync(new ServiceHeartbeatNotification(_lastBeat, Environment.ProcessId), ct);
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task WriteHeartbeatFileAsync(CancellationToken ct)
    {
        try
        {
            var content = System.Text.Json.JsonSerializer.Serialize(new
            {
                timestamp = _lastBeat.ToString("O"),
                pid = Environment.ProcessId
            });
            await File.WriteAllTextAsync(HeartbeatFilePath, content, System.Text.Encoding.UTF8, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write heartbeat file to {Path}", HeartbeatFilePath);
        }
    }
}
