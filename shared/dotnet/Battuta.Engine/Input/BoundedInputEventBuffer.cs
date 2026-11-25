using System.Threading.Channels;

namespace Battuta.Engine.Input;

/// <summary>
/// A non-blocking single-producer/single-consumer buffer for raw platform
/// events. When overloaded it keeps the newest input, which is preferable to
/// playing stale audio after a long backlog. DroppedCount feeds source health.
/// </summary>
public sealed class BoundedInputEventBuffer<T>
{
    private readonly Channel<T> _channel;
    private long _droppedCount;

    public BoundedInputEventBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _channel = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                SingleWriter = true,
                SingleReader = true,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            _ => Interlocked.Increment(ref _droppedCount));
    }

    public ChannelReader<T> Reader => _channel.Reader;

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryWrite(T input) => _channel.Writer.TryWrite(input);

    public bool TryComplete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
