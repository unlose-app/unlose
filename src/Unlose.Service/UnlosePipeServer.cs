using Unlose.Core.Interfaces;
using Unlose.Core.Ipc;
using Unlose.Core.Models;
using Microsoft.Extensions.Logging;
using System.IO.Pipes;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Unlose.Service;

public class UnlosePipeServer : IPipeServer
{
    private readonly ILogger<UnlosePipeServer> _logger;
    private readonly CommandDispatcher _dispatcher;
    private readonly IPipeSecurityHelper _securityHelper;
    private CancellationTokenSource? _cts;
    private const string PipeName = "unlosePipe";
    private readonly ConcurrentDictionary<Guid, SubscriberConnection> _subscribers = new();

    public UnlosePipeServer(
        ILogger<UnlosePipeServer> logger,
        CommandDispatcher dispatcher,
        IPipeSecurityHelper securityHelper)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _securityHelper = securityHelper;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("PipeServer listening on {PipeName}", PipeName);
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // SEC-001: create the named pipe with an ACL allowing only SYSTEM and Administrators to connect
                var pipeSecurity = new PipeSecurity();
                _securityHelper.ApplyAcl(pipeSecurity);
                var server = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity);
                await server.WaitForConnectionAsync(_cts.Token);

                if (!_securityHelper.VerifyClientSignature(server.SafePipeHandle))
                {
                    _logger.LogWarning("Pipe client signature verification failed; rejecting connection.");
                    server.Dispose();
                    continue;
                }

                _ = HandleClientAsync(server, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipeServer error");
            }
        }
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Dispose();
        }
        _subscribers.Clear();
        return Task.CompletedTask;
    }

    public async Task BroadcastAsync(IUnloseEvent evt, CancellationToken ct = default)
    {
        if (_subscribers.IsEmpty)
            return;

        var payload = JsonSerializer.Serialize(new PipeEnvelope
        {
            Type = evt.GetType().Name,
            Success = true,
            Payload = evt
        });

        foreach (var pair in _subscribers.ToArray())
        {
            var lockTaken = false;
            try
            {
                await pair.Value.Gate.WaitAsync(ct);
                lockTaken = true;
                await pair.Value.Writer.WriteLineAsync(payload);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Removing disconnected pipe subscriber {SubscriberId}", pair.Key);
                RemoveSubscriber(pair.Key);
            }
            finally
            {
                if (lockTaken)
                    pair.Value.Gate.Release();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            try
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) return;
                var msg = JsonSerializer.Deserialize<PipeMessage>(line);

                if (msg?.Command.Equals("SUBSCRIBE_EVENTS", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await HandleSubscriberAsync(pipe, reader, writer, msg, ct);
                    return;
                }

                var response = msg is null
                    ? new PipeResponse { Success = false, ErrorMessage = "Invalid message" }
                    : await _dispatcher.DispatchAsync(msg, ct);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling pipe client");
            }
        }
    }

    private async Task HandleSubscriberAsync(
        NamedPipeServerStream pipe,
        StreamReader reader,
        StreamWriter writer,
        PipeMessage message,
        CancellationToken ct)
    {
        var subscriberId = Guid.NewGuid();
        _subscribers[subscriberId] = new SubscriberConnection(pipe, writer);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeEnvelope
        {
            Type = "SUBSCRIBE_ACK",
            RequestId = message.RequestId,
            Success = true
        }));

        _logger.LogInformation("Pipe subscriber connected: {SubscriberId}", subscriberId);

        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            RemoveSubscriber(subscriberId);
        }
    }

    private void RemoveSubscriber(Guid subscriberId)
    {
        if (_subscribers.TryRemove(subscriberId, out var subscriber))
        {
            subscriber.Dispose();
            _logger.LogInformation("Pipe subscriber disconnected: {SubscriberId}", subscriberId);
        }
    }

    private sealed class SubscriberConnection : IDisposable
    {
        public SubscriberConnection(NamedPipeServerStream pipe, StreamWriter writer)
        {
            Pipe = pipe;
            Writer = writer;
        }

        public NamedPipeServerStream Pipe { get; }
        public StreamWriter Writer { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Dispose()
        {
            try { Pipe.Dispose(); } catch { }
            try { Gate.Dispose(); } catch { }
        }
    }
}
