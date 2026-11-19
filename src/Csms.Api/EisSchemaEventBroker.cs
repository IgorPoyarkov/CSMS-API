using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Csms.Api;

/// <summary>
/// Рассылка событий об обновлении XSD через SSE. Каждый подписчик получает
/// отдельный канал: медленный браузер не должен задерживать остальные.
/// </summary>
public sealed class EisSchemaEventBroker
{
    private readonly ConcurrentDictionary<Guid, Channel<EisSchemaUpdateEvent>> _subscribers = new();

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<EisSchemaUpdateEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[id] = channel;

        return new Subscription(this, id, channel);
    }

    public void Publish(EisSchemaUpdateEvent change)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(change);
        }
    }

    public sealed class Subscription(
        EisSchemaEventBroker owner,
        Guid id,
        Channel<EisSchemaUpdateEvent> channel) : IAsyncDisposable
    {
        public ChannelReader<EisSchemaUpdateEvent> Reader => channel.Reader;

        public ValueTask DisposeAsync()
        {
            owner._subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();

            return ValueTask.CompletedTask;
        }
    }
}
