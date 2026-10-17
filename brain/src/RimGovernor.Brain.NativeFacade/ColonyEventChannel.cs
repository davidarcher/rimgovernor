using System.Threading.Channels;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.NativeFacade;

/// <summary>
/// The colony events native pushes to the brain: a bounded queue the native mod owns, written on the game thread
/// without blocking, and a loss counter. A write that finds the queue full is dropped and counted; the brain plans a
/// Full step on any change in <see cref="Lost"/>. <see cref="Open"/> clears both, so a started or reloaded brain
/// begins empty and needs no history. The queue holds only shared-assembly records, so native never references the
/// brain's own types.
/// </summary>
public sealed class ColonyEventChannel
{
    /// <summary>The events the queue holds before a write is dropped and counted as lost.</summary>
    public const int Capacity = 1024;

    private readonly Channel<Clock.ClockEvent> _channel = Channel.CreateBounded<Clock.ClockEvent>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    private long _lost;

    /// <summary>The writes dropped since the last <see cref="Open"/>; it only grows between opens.</summary>
    public long Lost => Interlocked.Read(ref _lost);

    /// <summary>Queues <paramref name="value"/> without blocking; a full queue drops it and counts the loss.</summary>
    public void Publish(Clock.ClockEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_channel.Writer.TryWrite(value))
        {
            CountLost();
        }
    }

    /// <summary>Counts an event that could not be queued at all (one native failed to build).</summary>
    public void CountLost() => Interlocked.Increment(ref _lost);

    /// <summary>Drops every queued event, clears the loss counter and returns the reading side for the new subscriber.</summary>
    public ChannelReader<Clock.ClockEvent> Open()
    {
        while (_channel.Reader.TryRead(out _))
        {
        }
        Interlocked.Exchange(ref _lost, 0);
        return _channel.Reader;
    }
}
