using System.IO;
using Unlose.Core.Ipc;
using Unlose.Core.Config;
using Unlose.Core.Models;
using System.IO.Pipes;
using System.Text.Json;

namespace Unlose.UI;

/// <summary>
/// Lightweight Named Pipe client for the UI layer; communicates with the Service via unlosePipe
/// </summary>
public static class ServiceClient
{
    private const string PipeName = "unlosePipe";
    private const int ConnectTimeoutMs = 3000;
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "unlose",
        "config.json");
    private static readonly object SubscriptionLock = new();
    private static CancellationTokenSource? _subscriptionCts;
    private static Task? _subscriptionTask;

    public static event EventHandler<ServiceNotificationEventArgs>? NotificationReceived;

    /// <summary>Send a command and return a PipeResponse; returns an error response when the connection fails</summary>
    public static async Task<PipeResponse> SendAsync(
        string command,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ConnectTimeoutMs, ct);

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            var msg = new PipeMessage
            {
                Command    = command,
                Parameters = parameters ?? new Dictionary<string, string>()
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(msg));
            var raw = await reader.ReadLineAsync(ct);
            return raw is null
                ? Err("Service returned empty response.")
                : JsonSerializer.Deserialize<PipeResponse>(raw) ?? Err("Failed to deserialize response.");
        }
        catch (TimeoutException)
        {
            return Err("Cannot connect to Unlose service (timeout). Is the service running?");
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    private static PipeResponse Err(string msg) =>
        new PipeResponse { Success = false, ErrorMessage = msg };

    public static async Task<List<SnapshotRecord>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        var response = await SendAsync("LIST_SNAPSHOTS", ct: ct);
        return DeserializeList<SnapshotRecord>(response);
    }

    public static async Task<List<AuditEntry>> ListAuditLogAsync(int days = 30, CancellationToken ct = default)
    {
        var response = await SendAsync("LIST_AUDIT_LOG", new Dictionary<string, string>
        {
            ["days"] = days.ToString()
        }, ct);
        return DeserializeList<AuditEntry>(response);
    }

    public static async Task<List<MonitorEventRecord>> ListMonitorEventsAsync(int days = 1, int max = 200, CancellationToken ct = default)
    {
        var response = await SendAsync("LIST_MONITOR_EVENTS", new Dictionary<string, string>
        {
            ["days"] = days.ToString(),
            ["max"] = max.ToString()
        }, ct);
        return DeserializeList<MonitorEventRecord>(response);
    }

    public static async Task<List<MonitorEventRecord>> ListMonitorEventsAsync(int days, int max, string? eventType, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["days"] = days.ToString(),
            ["max"] = max.ToString()
        };

        if (!string.IsNullOrWhiteSpace(eventType))
            parameters["eventType"] = eventType;

        var response = await SendAsync("LIST_MONITOR_EVENTS", parameters, ct);
        return DeserializeList<MonitorEventRecord>(response);
    }

    public static async Task<List<AgentSessionRecord>> ListAgentSessionsAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        var response = await SendAsync("LIST_AGENT_SESSIONS", new Dictionary<string, string>
        {
            ["activeOnly"] = activeOnly.ToString()
        }, ct);
        return DeserializeList<AgentSessionRecord>(response);
    }

    public static Task<UnloseConfig> LoadConfigAsync(CancellationToken ct = default)
        => ConfigLoader.LoadAsync(ConfigFilePath, ct);

    public static void EnsureEventSubscriptionStarted()
    {
        lock (SubscriptionLock)
        {
            if (_subscriptionTask is { IsCompleted: false })
                return;

            _subscriptionCts?.Cancel();
            _subscriptionCts = new CancellationTokenSource();
            _subscriptionTask = Task.Run(() => RunEventSubscriptionLoopAsync(_subscriptionCts.Token));
        }
    }

    private static List<T> DeserializeList<T>(PipeResponse response)
    {
        if (!response.Success || string.IsNullOrWhiteSpace(response.Data))
            return new List<T>();

        try
        {
            return JsonSerializer.Deserialize<List<T>>(response.Data) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private static async Task RunEventSubscriptionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ConnectTimeoutMs, ct);

                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, leaveOpen: true);

                await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeMessage
                {
                    Command = "SUBSCRIBE_EVENTS",
                    RequestId = Guid.NewGuid().ToString("N")
                }));

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                        break;

                    ProcessNotificationLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Auto-reconnect while the service is not ready yet
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void ProcessNotificationLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("Type", out var typeProperty))
                return;

            var type = typeProperty.GetString();
            if (string.IsNullOrWhiteSpace(type) || string.Equals(type, "SUBSCRIBE_ACK", StringComparison.OrdinalIgnoreCase))
                return;

            JsonElement? payload = root.TryGetProperty("Payload", out var payloadElement)
                ? payloadElement.Clone()
                : null;

            NotificationReceived?.Invoke(null, new ServiceNotificationEventArgs(type, payload));
        }
        catch
        {
        }
    }

    public sealed record ServiceNotificationEventArgs(string Type, JsonElement? Payload);
}