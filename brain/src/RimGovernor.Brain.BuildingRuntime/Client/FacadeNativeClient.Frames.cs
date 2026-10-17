using System.Diagnostics;
using System.Threading.Channels;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Telemetry;
using P =RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// One frame taken off the handoff channel: the frame, a sequence number the readers compare against, and what
/// the readers derive from it once (the held tables and the emergency census).
/// </summary>
internal sealed class FrameView
{
    private readonly Lazy<FramePrepared> _prepared;
    private readonly Lazy<FrameCellGrid?> _grid;

    internal FrameView(long number, NativeFrame frame)
    {
        Number = number;
        Frame = frame;
        _prepared = new Lazy<FramePrepared>(() => FramePrepared.Of(frame.Snapshot), LazyThreadSafetyMode.ExecutionAndPublication);
        _grid = new Lazy<FrameCellGrid?>(() => frame.Snapshot.Grid is { } grid ? new FrameCellGrid(grid) : null, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The reader over the frame's whole-map grid, null for a frame without one.</summary>
    internal FrameCellGrid? Grid => _grid.Value;

    /// <summary>The order the feed took the frame in.</summary>
    internal long Number { get; }

    internal NativeFrame Frame { get; }

    internal PbObservations.BundleSnapshot Snapshot => Frame.Snapshot;

    /// <summary>The applied-write count the frame was captured at.</summary>
    internal long Writes => Frame.Writes;

    internal FramePrepared Prepared => _prepared.Value;
}

/// <summary>
/// What is derived from one frame before any read looked in it: a failure frame's
/// refusal, a frame that cannot be served at all (it is skipped until a newer one arrives), the world it describes,
/// the held keyed tables and the emergency census.
/// </summary>
internal sealed class FramePrepared
{
    private FramePrepared()
    {
    }

    /// <summary>The detail of a Failure the frame carried: a refusal naming the section, never a frame without it.</summary>
    internal string? FailureDetail { get; private init; }

    /// <summary>Why the frame is skipped (an invalid pawn row under an emergency census, an undecodable census).</summary>
    internal Exception? Problem { get; private init; }

    /// <summary>A refusal the emergency decoder raised: surfaced to the reader.</summary>
    internal Bridge.Contracts.ReadRefusedException? DecodeRefusal { get; private init; }

    internal PbCommon.Identity? World { get; private init; }

    internal FrameHeld Held { get; private init; } = FrameHeld.Empty;

    /// <summary>
    /// The frame's emergency census decoded against <see cref="Held"/>'s pawn table, with the pending drop pods of the
    /// frame's combat events; null when the frame carries no census. The routine and combat reads share it.
    /// </summary>
    internal EmergencyObservation? Emergency { get; private init; }

    internal static FramePrepared Of(PbObservations.BundleSnapshot v)
    {
        if (v.Failure is { } failure)
        {
            return new FramePrepared { FailureDetail = failure.Detail ?? "" };
        }
        var context = v.Context!;
        var held = FrameHeld.Of(v, context);
        EmergencyObservation? emergency = null;
        if (v.Emergency is not null)
        {
            try
            {
                emergency = FacadeNativeClient.PodsPending(FacadeNativeClient.EmergencyDecode.DecodeEmergencyStatus(v.Emergency, held.Pawns), v.CombatEvents);
            }
            catch (Bridge.Contracts.ReadRefusedException refusal)
            {
                return new FramePrepared { DecodeRefusal = refusal, World = context.Identity };
            }
            catch (BridgeContractException problem)
            {
                return new FramePrepared { Problem = problem, World = context.Identity };
            }
        }
        return new FramePrepared { World = context.Identity, Held = held, Emergency = emergency };
    }
}

/// <summary>
/// Because every handed-over frame is whole, the keyed tables are
/// indexed straight from the frame's lists and the envelopes are the frame's sections without their rows.
/// </summary>
internal sealed class FrameHeld
{
    internal static readonly FrameHeld Empty = new();

    internal Pawns Pawns { get; private init; }

    internal PbObservations.PawnSnapshot? PawnMeta { get; private init; }

    internal Buildings Buildings { get; private init; }

    internal PbObservations.BuildingsSnapshot? BuildMeta { get; private init; }

    internal Things Things { get; private init; }

    internal PbObservations.ThingsSnapshot? ThingsMeta { get; private init; }

    internal static FrameHeld Of(PbObservations.BundleSnapshot v, PbCommon.ObservationContext context) => new()
    {
        Pawns = v.Pawns is { } pawns ? new Pawns(Index(pawns.Pawns, row => row.Pawn.Id)) : default,
        PawnMeta = v.Pawns is { } pawnMeta ? pawnMeta with { Context = context, Pawns = default } : null,
        Buildings = v.Buildings is { } buildings ? new Buildings(Index(buildings.Buildings, row => row.Building.Id)) : default,
        BuildMeta = v.Buildings is { } buildMeta ? buildMeta with { Context = context, Buildings = default } : null,
        Things = v.Things is { } things ? new Things(Index(things.Things, row => row.Entity.Id)) : default,
        ThingsMeta = v.Things is not null ? new PbObservations.ThingsSnapshot { Context = context } : null,
    };

    /// <summary>A keyed table of the rows: a row with no id is not addressable and never enters it; a repeated id keeps the last row.</summary>
    private static Table<TRow> Index<TRow>(IEnumerable<TRow> rows, Func<TRow, string> id)
    {
        var table = default(Table<TRow>);
        foreach (var row in rows)
        {
            if (id(row) is { Length: > 0 } key)
            {
                table = table.Set(key, row);
            }
        }
        return table;
    }
}

/// <summary>
/// The frame reader (the "one small internal frame reader class that owns the channel reads"). The facade hands
/// frames over a latest-wins channel of one with a single reader, so concurrent core reads must not each take from
/// it: this class is that one reader. It keeps the newest frame it has seen and a sequence number; a core read looks
/// at the cached frame and, when it needs a newer one, waits behind a single pump that drains the channel into the
/// cache. Two concurrent reads therefore both see every frame instead of stealing it from each other, and a frame
/// is decoded at most once (<see cref="FrameView.Prepared"/>).
/// </summary>
internal sealed class FrameFeed(ChannelReader<NativeFrame> reader) : IDisposable
{
    public void Dispose() => _pump.Dispose();

    private readonly object _gate = new();
    private readonly SemaphoreSlim _pump = new(1, 1);
    private FrameView? _latest;
    private long _number;

    /// <summary>The newest frame taken off the channel so far, null before the first or after a resubscription.</summary>
    internal FrameView? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>True once the channel is completed: no frame will ever arrive again.</summary>
    internal bool Completed => reader.Completion.IsCompleted;

    /// <summary>The newest frame, after taking whatever is queued.</summary>
    internal async Task<FrameView?> LatestAsync(CancellationToken cancellationToken)
    {
        await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Drain();
        }
        finally
        {
            _pump.Release();
        }
        return Latest;
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for a frame past sequence <paramref name="after"/>; false when none
    /// arrived (or the channel completed).
    /// </summary>
    internal async Task<bool> WaitNewerAsync(long after, TimeSpan wait, CancellationToken cancellationToken)
    {
        if (Latest is { } current && current.Number > after)
        {
            return true;
        }
        await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Drain();
            if (Latest is { } drained && drained.Number > after)
            {
                return true;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(wait);
            try
            {
                if (!await reader.WaitToReadAsync(limit.Token).ConfigureAwait(false))
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            Drain();
            return Latest is { } arrived && arrived.Number > after;
        }
        finally
        {
            _pump.Release();
        }
    }

    /// <summary>Drops the cached frame and everything queued: a resubscription's earlier frames lack what it added.</summary>
    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (reader.TryRead(out _))
            {
            }
            lock (_gate)
            {
                _latest = null;
            }
        }
        finally
        {
            _pump.Release();
        }
    }

    private void Drain()
    {
        while (reader.TryRead(out var frame))
        {
            lock (_gate)
            {
                _latest = new FrameView(++_number, frame);
            }
        }
    }
}

public sealed partial class FacadeNativeClient : IDisposable
{
    /// <summary>Releases the frame reader's and the stream opener's gates.</summary>
    public void Dispose()
    {
        _frameOpenGate.Dispose();
        _frameFeed?.Dispose();
    }

    /// <summary>The main-thread time one frame may spend pathing facility x colonist pairs.</summary>
    internal const uint RoutePathBudgetMs = 100;

    internal const uint HuntRouteBudgetMs = 50;

    internal const uint HerdRadius = 25;

    internal const uint LinesOfFireBudgetMs = 15;

    /// <summary>One wait for the next frame.</summary>
    private static readonly TimeSpan FrameWriteWait = TimeSpan.FromSeconds(1);

    /// <summary>The space between attempts to open the stream.</summary>
    private static readonly TimeSpan FrameRetry = TimeSpan.FromSeconds(5);

    private readonly object _frameGate = new();
    private readonly SemaphoreSlim _frameOpenGate = new(1, 1);
    private FrameFeed? _frameFeed;
    private bool _frameOpened;
    private bool _frameStale;
    private long _frameNextAttempt;
    private string _frameOpenError = "";

    /// <summary>
    /// How long a frame read waits for a frame that answers it before it is
    /// unavailable.
    /// </summary>
    internal TimeSpan FrameReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Where a frame read that gave up writes its <c>native_frame</c> miss row; null for a bare client.</summary>
    internal TelemetryEmitter? Telemetry { get; set; }

    /// <summary>
    /// Whether reads are served from the snapshot stream. Off, the routine, combat,
    /// planning-window and table reads are unavailable and the colony facts, status and entity lists are read live.
    /// </summary>
    internal bool UseFrames { get; init; } = true;

    private FrameFeed Feed => LazyInitializer.EnsureInitialized(ref _frameFeed, () => new FrameFeed(Facade.Frames))!;

    /// <summary>
    /// Subscribes in process to the native snapshot
    /// stream. <c>PlannerParts.OpenFrames</c> calls it once at start; a frame read calls it again by itself when the
    /// stream is not open or the subscription grew. The frames captured before it are dropped.
    /// </summary>
    internal async Task OpenFramesAsync(CancellationToken cancellationToken)
    {
        await _frameOpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_frameGate)
            {
                _frameStale = false;
            }
            var request = new PbObservations.SnapshotStreamRequest
            {
                RoutePathBudgetMs = RoutePathBudgetMs,
                HuntRouteBudgetMs = ColonyFactsParameters.HuntRouteBudgetMs,
                HuntPredatorMarginCells = ColonyFactsParameters.HuntPredatorMarginCells,
                HerdRadius = ColonyFactsParameters.HerdRadius,
                LinesOfFireBudgetMs = LinesOfFireBudgetMs,
            };
            PbObservations.SnapshotStreamReply reply;
            reply = await Facade.OpenFramesAsync(request, cancellationToken).ConfigureAwait(false);
            var outcome = reply.Outcome;
            if (outcome.Opened is null)
            {
                lock (_frameGate)
                {
                    _frameStale = true;
                }
                throw Unavailable(outcome.Unavailable!);
            }
            await Feed.ResetAsync(cancellationToken).ConfigureAwait(false);
            lock (_frameGate)
            {
                _frameOpened = true;
                _frameNextAttempt = 0;
                _frameOpenError = "";
            }
        }
        finally
        {
            _frameOpenGate.Release();
        }
    }

    /// <summary>
    /// Asks native to capture the writes deferred so far.
    /// The reply does not wait for the capture.
    /// </summary>
    internal async Task FlushSnapshotAsync(CancellationToken cancellationToken)
    {
        await Facade.FlushFramesAsync(new PbObservations.FlushSnapshotRequest(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens (or resubscribes) the stream when it is not open or the subscription grew,
    /// spacing failed attempts by <see cref="FrameRetry"/>. A failed attempt is not an error of the read: the read
    /// waits for a frame and reports the last open failure if none arrives.
    /// </summary>
    private async Task EnsureFramesAsync(CancellationToken cancellationToken)
    {
        bool due;
        lock (_frameGate)
        {
            due = (!_frameOpened || _frameStale) && Stopwatch.GetTimestamp() >= _frameNextAttempt;
        }
        if (!due)
        {
            return;
        }
        try
        {
            await OpenFramesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is BridgeRefusedException or BridgeUnavailableException or BridgeContractException)
        {
            lock (_frameGate)
            {
                _frameNextAttempt = Stopwatch.GetTimestamp() + (long)(FrameRetry.TotalSeconds * Stopwatch.Frequency);
                _frameOpenError = failure.Message;
            }
        }
    }

    /// <summary>What a frame answers for one read: whether it carries the answer, the answer, and whether the frame carries the method in another shape.</summary>
    internal readonly record struct FrameLookup<T>(bool Found, T? Value, bool Carries);

    /// <summary>The outcome of a frame read: served by a frame, or not (the caller reads live).</summary>
    internal readonly record struct FrameServed<T>(bool Served, T? Value);

    /// <summary>
    /// Waits for the newest frame that answers the read
    /// (<paramref name="lookup"/>) and returns the answer. A frame carrying a Failure is a
    /// <see cref="Bridge.Contracts.ReadRefusedException"/> naming the section; a frame of another world is <see cref="BridgeRefusedException"/>;
    /// no frame within <see cref="FrameReadTimeout"/> is <see cref="BridgeUnavailableException"/>. With
    /// <paramref name="fallback"/>, a frame of the same world that carries the method only in another shape, while
    /// no subscription change is pending, is not served and the caller reads live.
    /// </summary>
    internal async Task<FrameServed<T>> FrameReadAsync<T>(
        string name,
        PbCommon.Identity identity,
        bool fallback,
        Func<FrameView, FrameLookup<T>> lookup,
        CancellationToken cancellationToken)
    {
        var waitStarted = Stopwatch.GetTimestamp();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(FrameReadTimeout);
        var token = limit.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await EnsureFramesAsync(token).ConfigureAwait(false);
                bool pending;
                lock (_frameGate)
                {
                    pending = _frameStale;
                }
                var frame = await Feed.LatestAsync(token).ConfigureAwait(false);
                long after = 0;
                if (frame is not null)
                {
                    after = frame.Number;
                    var prepared = frame.Prepared;
                    if (prepared.FailureDetail is not null)
                    {
                        throw new Bridge.Contracts.ReadRefusedException("snapshot_frame", prepared.FailureDetail, NoReceipt);
                    }
                    if (prepared.DecodeRefusal is { } decoded)
                    {
                        throw new Bridge.Contracts.ReadRefusedException(decoded.Tool, decoded.Cause, NoReceipt);
                    }
                    if (prepared.Problem is null)
                    {
                        var found = lookup(frame);
                        if (found.Found)
                        {
                            return new FrameServed<T>(true, found.Value);
                        }
                        if (prepared.World is not null && !WireValidation.SameIdentity(prepared.World, identity))
                        {
                            throw new BridgeRefusedException($"{name} asked for another world than the snapshot frames describe");
                        }
                        if (prepared.World is not null && fallback && found.Carries && !pending)
                        {
                            return new FrameServed<T>(false, default);
                        }
                    }
                }
                if (!await Feed.WaitNewerAsync(after, FrameWriteWait, token).ConfigureAwait(false) && Feed.Completed)
                {
                    throw new BridgeUnavailableException($"snapshot frames closed before one carried {name}");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var latest = Feed.Latest;
            string detail;
            lock (_frameGate)
            {
                detail = _frameOpened ? "no snapshot frame carried " + name : "snapshot stream not open for " + name + (_frameOpenError.Length == 0 ? "" : ": " + _frameOpenError);
            }
            Telemetry?.Event(
                TelemetryLevel.Warn,
                "native-client",
                "native_frame",
                "a frame read timed out",
                [
                    new("outcome", "miss"),
                    new("native_tool", name),
                    new("why", detail),
                    new("waited_ms", (long)Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds),
                    new("latest_frame", latest?.Number ?? 0),
                    new("latest_writes", latest?.Writes ?? 0),
                ]);
            throw new BridgeUnavailableException(detail);
        }
    }

    /// <summary>
    /// A frame read of one whole section: found when <paramref name="section"/> returns it, and a frame without it
    /// is waited past.
    /// </summary>
    internal async Task<T> FrameSectionAsync<T>(
        string name,
        PbCommon.Identity identity,
        Func<FrameView, T?> section,
        CancellationToken cancellationToken)
        where T : class
    {
        var served = await FrameReadAsync(
            name,
            identity,
            fallback: true,
            view =>
            {
                var value = section(view);
                return new FrameLookup<T>(value is not null, value, value is not null);
            },
            cancellationToken).ConfigureAwait(false);
        return served.Value!;
    }
}
