using System.Threading.Channels;

namespace RimGovernor.Brain.NativeFacade;

/// <summary>
/// The colony events native pushes to the brain: a bounded queue the native mod owns, written on the game thread
/// without blocking, and a loss counter. A write that finds the queue full is dropped and counted; the brain plans a
/// Full step on any change in <see cref="Lost"/>. <see cref="Open"/> swaps in a fresh queue and clears the counter, so a
/// started or reloaded brain begins empty and needs no history. The queue holds only shared-assembly records
/// (<see cref="ColonyEvent"/>), so native never references the brain's own types.
/// </summary>
public sealed class ColonyEventChannel
{
    /// <summary>The events the queue holds before a write is dropped and counted as lost.</summary>
    public const int Capacity = 1024;

    private Channel<ColonyEvent> _channel = Create();

    private long _lost;

    /// <summary>The events dropped since the last <see cref="Open"/>.</summary>
    public long Lost => Interlocked.Read(ref _lost);

    /// <summary>Queues <paramref name="value"/> without blocking; a full queue drops it and counts the loss.</summary>
    public void Publish(ColonyEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var channel = Volatile.Read(ref _channel);
        // A write that raced an open went to the replaced queue: it predates the open, which drops it like any queued event.
        if (!channel.Writer.TryWrite(value) && ReferenceEquals(channel, Volatile.Read(ref _channel)))
        {
            Interlocked.Increment(ref _lost);
        }
    }

    /// <summary>
    /// Swaps in a fresh queue, clears the loss counter and returns the new reading side. The replaced queue is completed,
    /// so a previous subscriber's pending wait finishes with no more data and native keeps no reference to its waiter:
    /// an unloaded brain is not pinned by its last read.
    /// </summary>
    public ChannelReader<ColonyEvent> Open()
    {
        var fresh = Create();
        var replaced = Interlocked.Exchange(ref _channel, fresh);
        _ = replaced.Writer.TryComplete();
        Interlocked.Exchange(ref _lost, 0);
        return fresh.Reader;
    }

    private static Channel<ColonyEvent> Create() => Channel.CreateBounded<ColonyEvent>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
}
