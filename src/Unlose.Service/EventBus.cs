using System.Threading.Channels;
using Unlose.Core.Models;

namespace Unlose.Service;

/// <summary>
/// Channel-based bounded event bus.
/// Bounded buffer of 10000 to prevent high-frequency event buildup from causing OOM.
/// Producers call PublishAsync; consumers iterate asynchronously via Reader.
/// </summary>
public sealed class EventBus
{
    private readonly Channel<IUnloseEvent> _channel;

    public EventBus()
    {
        _channel = Channel.CreateBounded<IUnloseEvent>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
    }

    public ChannelReader<IUnloseEvent> Reader => _channel.Reader;

    /// <summary>Publish an event (non-blocking; drops the oldest event when full)</summary>
    public void Publish(IUnloseEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>Publish an event (asynchronously waits for a write slot)</summary>
    public ValueTask PublishAsync(IUnloseEvent evt, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(evt, ct);

    // Backward compatibility: legacy code publishes via AlertRecord; real alert semantics are preserved
    public void PublishAlert(Unlose.Core.Models.AlertRecord alert)
        => Publish(new AlertRaisedEvent(alert));
}

