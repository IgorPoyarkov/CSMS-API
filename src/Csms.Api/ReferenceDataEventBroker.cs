using System.Collections.Concurrent;
using System.Threading.Channels;
using Csms.Application;

namespace Csms.Api;

/// <summary>SSE-рассылка фактов импорта новых версий справочников.</summary>
public sealed class ReferenceDataEventBroker
{
    private readonly ConcurrentDictionary<Guid, Channel<ReferenceDataUpdateEvent>> _subscribers = new();

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<ReferenceDataUpdateEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[id] = channel;
        return new Subscription(this, id, channel);
    }

    public void Publish(ReferenceDataUpdateEvent change)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(change);
        }
    }

    public sealed class Subscription(
        ReferenceDataEventBroker owner,
        Guid id,
        Channel<ReferenceDataUpdateEvent> channel) : IAsyncDisposable
    {
        public ChannelReader<ReferenceDataUpdateEvent> Reader => channel.Reader;

        public ValueTask DisposeAsync()
        {
            owner._subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
