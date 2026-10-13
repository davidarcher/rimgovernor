using System.Threading.Channels;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.NativeFacade;

/// <summary>
/// One captured frame handed to the planner worker: the whole <see cref="Obs.BundleSnapshot"/> (every section
/// carried, the cell grid a keyframe) and what the capture cost. Immutable by convention: the game thread built
/// it and nothing mutates it after it is published.
/// </summary>
/// <param name="Snapshot">The captured frame.</param>
/// <param name="Writes">The applied-write count the frame was captured at (the stream's write watermark).</param>
/// <param name="CaptureMicros">Game-thread microseconds the capture took.</param>
public sealed record NativeFrame(Obs.BundleSnapshot Snapshot, long Writes, uint CaptureMicros);

/// <summary>
/// The latest-wins slot between the native encoder worker (the single writer) and the planner worker (the single
/// reader): a bounded channel of one that drops the older frame when a newer one arrives, so the game side never
/// waits on the brain and the brain never reads a stale frame while a newer one is ready.
/// </summary>
public sealed class FrameHandoff
{
    private readonly Channel<NativeFrame> _channel = Channel.CreateBounded<NativeFrame>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false,
    });

    private int _attached;

    /// <summary>The reading side, for the planner worker.</summary>
    public ChannelReader<NativeFrame> Reader => _channel.Reader;

    /// <summary>True once a subscriber has opened frames; the capture side captures for the handoff only while this holds.</summary>
    public bool Attached => Volatile.Read(ref _attached) != 0;

    /// <summary>Marks a subscriber present.</summary>
    public void Attach() => Volatile.Write(ref _attached, 1);

    /// <summary>Publishes a frame, replacing one the reader has not taken.</summary>
    public void Publish(NativeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_channel.Writer.TryWrite(frame))
        {
            throw new InvalidOperationException("The frame handoff is closed.");
        }
    }
}
