using System.Diagnostics;
using System.Threading.Channels;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

/// <summary>
/// What the frame path needs from decoders that live in other groups of the typed client (the emergency, pawn,
/// population, research, trader, world progression, ideology, zone and lines-of-fire decoders and the definition
/// catalog read). The frame readers call them exactly where Go's <c>frames.go</c>, <c>frame_sections.go</c>,
/// <c>pawn_table.go</c> and <c>thing_table.go</c> called the package functions of the same names. The lead binds
/// this interface to the sibling core methods in <c>FacadeNativeClient.Interfaces.cs</c> and assigns it to
/// <see cref="FacadeNativeClient.FrameSections"/>. Every validator throws <see cref="BridgeContractException"/> for a
/// malformed row and may throw <see cref="Refusal"/> where the Go decoder returned one.
/// </summary>
internal interface IFrameSections
{
    /// <summary>Go <c>checkPawnRow</c>: one pawn table row against the frame that carried it.</summary>
    void CheckPawnRow(PbObservations.PawnState row, PbCommon.ObservationContext context);

    /// <summary>Go <c>checkBuiltRow</c>: one building table row against the frame that carried it.</summary>
    void CheckBuiltRow(PbObservations.BuildingState row, PbCommon.ObservationContext context);

    /// <summary>Go <c>checkBuildingListRow</c>: a building row as the entity list read returns it.</summary>
    void CheckBuildingListRow(PbObservations.BuildingState row);

    /// <summary>Go <c>pawnsEntity</c>: an entity head (id, def, label, position) against the frame's context.</summary>
    void CheckEntity(PbObservations.EntityRef? entity, PbCommon.ObservationContext context);

    /// <summary>Go <c>PawnTable</c>: validates a whole pawn snapshot against the identity and indexes its rows.</summary>
    Pawns PawnTable(PbObservations.PawnSnapshot? snapshot, PbCommon.Identity identity);

    /// <summary>Go <c>DecodeEmergencyStatus</c>.</summary>
    EmergencyObservation DecodeEmergencyStatus(PbObservations.StatusSnapshot snapshot, Pawns pawns, PbCommon.Identity identity);

    /// <summary>Go <c>decodePopulation</c>.</summary>
    PrisonerCensus DecodePopulation(PbObservations.PopulationSnapshot snapshot, Pawns pawns, DefinitionCatalog catalog);

    /// <summary>Go <c>readResearchSnapshot</c>.</summary>
    ResearchRead DecodeResearch(PbObservations.ResearchSnapshot snapshot, PbCommon.Identity identity, DefinitionCatalog catalog);

    /// <summary>Go <c>decodeTraders</c>.</summary>
    TradersRead DecodeTraders(PbObservations.TradersSnapshot snapshot, PbCommon.Identity identity);

    /// <summary>Go <c>worldProgressionSelected</c>.</summary>
    WorldProgressionRead DecodeWorldProgression(PbObservations.WorldProgressionSnapshot snapshot, PbCommon.Identity identity);

    /// <summary>Go <c>DecodeIdeology</c>.</summary>
    P.Ideoligion? DecodeIdeology(PbObservations.IdeologySnapshot snapshot, PbCommon.Identity identity, DefinitionCatalog catalog);

    /// <summary>Go <c>decodeZones</c>.</summary>
    ZonesRead DecodeZones(PbObservations.ZonesSnapshot snapshot, PbCommon.Identity identity);

    /// <summary>Go <c>validateLinesOfFire</c>: the validated lines for the distinct firing and approach cells.</summary>
    IReadOnlyList<Bridge.Contracts.LineOfFire> ValidateLinesOfFire(PbObservations.LinesOfFireSnapshot snapshot, PbCommon.Identity identity, IReadOnlyList<Cell> firing, IReadOnlyList<Cell> approach);

    /// <summary>Go <c>Client.DefinitionCatalog</c>: the load's definition catalog, read when not yet held.</summary>
    Task<DefinitionCatalog> DefinitionCatalogAsync(PbCommon.Identity identity, CancellationToken cancellationToken);

    /// <summary>Go <c>emergencyRequest</c>: the status request the emergency census (and the frame's emergency section) is read under.</summary>
    PbObservations.StatusRequest EmergencyRequest(PbCommon.Identity identity);

    /// <summary>
    /// Go <c>Client.ReadClockStatus</c> as <c>ReadStep</c> uses it: the owned clock status. An unavailable clock status
    /// throws <see cref="BridgeUnavailableException"/> (the step then carries none); any other failure throws as it is.
    /// </summary>
    Task<PbClock.Status> ReadClockStatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken);
}

/// <summary>
/// One frame taken off the handoff channel: the frame, a sequence number the readers compare against, and what
/// the readers derive from it once (the held tables and the emergency census).
/// </summary>
internal sealed class FrameView
{
    private readonly Lazy<FramePrepared> _prepared;
    private readonly Lazy<FrameGridView> _grid;

    internal FrameView(long number, NativeFrame frame, Func<IFrameSections> sections)
    {
        Number = number;
        Frame = frame;
        _prepared = new Lazy<FramePrepared>(() => FramePrepared.Of(frame.Snapshot, sections()), LazyThreadSafetyMode.ExecutionAndPublication);
        _grid = new Lazy<FrameGridView>(() => FrameGridView.Of(frame.Snapshot), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The frame's whole-map grid, decoded once (Go <c>gridHold.apply</c>).</summary>
    internal FrameGridView Grid => _grid.Value;

    /// <summary>The order the feed took the frame in (Go <c>snapshotshm.Frame.Number</c>).</summary>
    internal long Number { get; }

    internal NativeFrame Frame { get; }

    internal PbObservations.BundleSnapshot Snapshot => Frame.Snapshot;

    /// <summary>The applied-write count the frame was captured at.</summary>
    internal long Writes => Frame.Writes;

    internal FramePrepared Prepared => _prepared.Value;
}

/// <summary>
/// A frame's decoded grid, or why it could not be. A frame without a grid, and a frame whose grid is a delta (the
/// facade never hands one over), have neither.
/// </summary>
internal sealed record FrameGridView(FrameCellGrid? Grid, Exception? Error)
{
    /// <summary>Go <c>gridHold.apply</c> for a whole frame: the sky glow is validated but kept out of the cells.</summary>
    internal static FrameGridView Of(PbObservations.BundleSnapshot v)
    {
        if (v.Grid is not { } wire)
        {
            return new FrameGridView(null, null);
        }
        if (v.SkyGlow is not { } sky || double.IsNaN(sky) || sky < 0 || sky > 1)
        {
            return new FrameGridView(null, BridgeFunctions.Contract("frame grid sky glow"));
        }
        if (!FrameCellGrid.Complete(wire))
        {
            return new FrameGridView(null, null);
        }
        try
        {
            return new FrameGridView(FrameCellGrid.Apply(wire), null);
        }
        catch (BridgeContractException problem)
        {
            return new FrameGridView(null, problem);
        }
    }
}

/// <summary>
/// What Go's <c>frameStream.frameTable</c> derived from one frame before any read looked in it: a failure frame's
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

    /// <summary>Why the frame is skipped (invalid context, an invalid pawn row under an emergency census, an undecodable census).</summary>
    internal Exception? Problem { get; private init; }

    /// <summary>A refusal the emergency decoder raised: surfaced to the reader, like Go's refused frame.</summary>
    internal Refusal? DecodeRefusal { get; private init; }

    internal PbCommon.Identity? World { get; private init; }

    internal FrameHeld Held { get; private init; } = FrameHeld.Empty;

    internal EmergencyObservation Emergency { get; private init; } = new(null, new P.EmergencyFacts());

    internal static FramePrepared Of(PbObservations.BundleSnapshot v, IFrameSections sections)
    {
        if (v.Failure is { } failure)
        {
            return new FramePrepared { FailureDetail = failure.Detail ?? "" };
        }
        try
        {
            BridgeFunctions.ValidateContext(v.Context);
        }
        catch (BridgeContractException problem)
        {
            return new FramePrepared { Problem = problem };
        }
        var context = v.Context!;
        var held = FrameHeld.Of(v, context, sections);
        var emergency = new EmergencyObservation(null, new P.EmergencyFacts());
        if (v.Emergency is not null)
        {
            if (held.PawnError is not null)
            {
                return new FramePrepared { Problem = held.PawnError, World = context.Identity };
            }
            try
            {
                emergency = sections.DecodeEmergencyStatus(v.Emergency, held.Pawns, context.Identity!);
            }
            catch (Refusal refusal)
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
/// Go's <c>heldTables</c> without the hold: because every handed-over frame is whole, the keyed tables are
/// indexed straight from the frame's lists, each row checked once, and the envelopes are the frame's sections
/// without their rows.
/// </summary>
internal sealed class FrameHeld
{
    internal static readonly FrameHeld Empty = new();

    internal Pawns Pawns { get; private init; }

    internal PbObservations.PawnSnapshot? PawnMeta { get; private init; }

    internal Exception? PawnError { get; private init; }

    internal Buildings Buildings { get; private init; }

    internal PbObservations.BuildingsSnapshot? BuildMeta { get; private init; }

    internal Exception? BuildError { get; private init; }

    /// <summary>The first row the building list read would refuse.</summary>
    internal Exception? BuildListError { get; private init; }

    internal Things Things { get; private init; }

    internal PbObservations.ThingsSnapshot? ThingsMeta { get; private init; }

    internal Exception? ThingError { get; private init; }

    /// <summary>Go <c>heldTables.err</c>: the first invalid pawn or thing row.</summary>
    internal Exception? Error => PawnError ?? ThingError;

    internal static FrameHeld Of(PbObservations.BundleSnapshot v, PbCommon.ObservationContext context, IFrameSections sections)
    {
        var held = new FrameHeld();
        if (v.Pawns is { } pawns)
        {
            var (table, error, _) = HeldRows.Load(pawns.Pawns, row => row.Pawn?.Id ?? "", row => sections.CheckPawnRow(row, context), null);
            held = held.With(pawnTable: new Pawns(table), pawnMeta: pawns with { Context = context, Pawns = default, Removed = default }, pawnError: error);
        }
        if (v.Buildings is { } buildings)
        {
            var (table, error, listError) = HeldRows.Load(buildings.Buildings, row => row.Building?.Id ?? "", row => sections.CheckBuiltRow(row, context), sections.CheckBuildingListRow);
            held = held.With(buildingTable: new Buildings(table), buildMeta: buildings with { Context = context, Buildings = default, Removed = default }, buildError: error, buildListError: listError);
        }
        if (v.Things is { } things)
        {
            var (table, error, _) = HeldRows.Load(things.Things, row => row.Entity?.Id ?? "", row => FacadeNativeClient.CheckThingRow(row, context, sections), null);
            held = held.With(thingTable: new Things(table), thingsMeta: new PbObservations.ThingsSnapshot { Context = context }, thingError: error);
        }
        return held;
    }

    /// <summary>Go <c>heldTables.pawnList</c>: the pawn table as a list snapshot in id order, null when the frame carries none.</summary>
    internal PbObservations.PawnSnapshot? PawnList() =>
        PawnMeta is null ? null : PawnMeta with { Pawns = [.. Pawns.Table.Sorted()] };

    /// <summary>Go <c>heldTables.buildingList</c>.</summary>
    internal PbObservations.BuildingsSnapshot? BuildingList() =>
        BuildMeta is null ? null : BuildMeta with { Buildings = [.. Buildings.Table.Sorted()] };

    private FrameHeld With(
        Pawns? pawnTable = null,
        PbObservations.PawnSnapshot? pawnMeta = null,
        Exception? pawnError = null,
        Buildings? buildingTable = null,
        PbObservations.BuildingsSnapshot? buildMeta = null,
        Exception? buildError = null,
        Exception? buildListError = null,
        Things? thingTable = null,
        PbObservations.ThingsSnapshot? thingsMeta = null,
        Exception? thingError = null) =>
        new()
        {
            Pawns = pawnTable ?? Pawns,
            PawnMeta = pawnMeta ?? PawnMeta,
            PawnError = pawnError ?? PawnError,
            Buildings = buildingTable ?? Buildings,
            BuildMeta = buildMeta ?? BuildMeta,
            BuildError = buildError ?? BuildError,
            BuildListError = buildListError ?? BuildListError,
            Things = thingTable ?? Things,
            ThingsMeta = thingsMeta ?? ThingsMeta,
            ThingError = thingError ?? ThingError,
        };
}

/// <summary>Go <c>heldRows</c>: a keyed row table loaded from a keyframe, with the rows that failed their check kept apart.</summary>
internal static class HeldRows
{
    /// <summary>
    /// Go <c>heldRows.load</c>. A row with no id is not addressable and never enters the table; a repeated id is a
    /// bad row of its own. <paramref name="check"/> and <paramref name="listCheck"/> throw
    /// <see cref="BridgeContractException"/>; the first failure in id order is returned for each.
    /// </summary>
    internal static (Table<TRow> Table, Exception? Error, Exception? ListError) Load<TRow>(
        IEnumerable<TRow> rows,
        Func<TRow, string> id,
        Action<TRow> check,
        Action<TRow>? listCheck)
    {
        var table = default(Table<TRow>);
        var bad = new Dictionary<string, Exception>(StringComparer.Ordinal);
        var listBad = new Dictionary<string, Exception>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = id(row);
            if (key.Length != 0 && table.Has(key))
            {
                bad["duplicate " + key] = BridgeFunctions.Contract("duplicate row \"" + key + "\"");
            }
            bad.Remove(key);
            listBad.Remove(key);
            try
            {
                check(row);
            }
            catch (BridgeContractException problem)
            {
                bad[key] = problem;
            }
            if (listCheck is not null)
            {
                try
                {
                    listCheck(row);
                }
                catch (BridgeContractException problem)
                {
                    listBad[key] = problem;
                }
            }
            if (key.Length != 0)
            {
                table = table.Set(key, row);
            }
        }
        return (table, First(bad), First(listBad));
    }

    private static Exception? First(Dictionary<string, Exception> bad)
    {
        string? first = null;
        Exception? error = null;
        foreach (var (key, problem) in bad)
        {
            if (error is null || string.CompareOrdinal(key, first) < 0)
            {
                (first, error) = (key, problem);
            }
        }
        return error;
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
internal sealed class FrameFeed(ChannelReader<NativeFrame> reader, Func<IFrameSections> sections) : IDisposable
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
                _latest = new FrameView(++_number, frame, sections);
            }
        }
    }
}

internal sealed partial class FacadeNativeClient : IDisposable
{
    /// <summary>Releases the frame reader's and the stream opener's gates.</summary>
    public void Dispose()
    {
        _frameOpenGate.Dispose();
        _frameFeed?.Dispose();
    }

    /// <summary>Go <c>RoutePathBudgetMS</c>: the main-thread time one frame may spend pathing facility x colonist pairs.</summary>
    internal const uint RoutePathBudgetMs = 100;

    /// <summary>Go <c>HuntRouteBudgetMS</c>.</summary>
    internal const uint HuntRouteBudgetMs = 50;

    /// <summary>Go <c>HerdRadius</c>.</summary>
    internal const uint HerdRadius = 25;

    /// <summary>Go <c>LinesOfFireBudgetMS</c>.</summary>
    internal const uint LinesOfFireBudgetMs = 15;

    /// <summary>Go <c>frameWriteWait</c>: one wait for the next frame.</summary>
    private static readonly TimeSpan FrameWriteWait = TimeSpan.FromSeconds(1);

    /// <summary>Go <c>frameRetry</c>: the space between attempts to open the stream.</summary>
    private static readonly TimeSpan FrameRetry = TimeSpan.FromSeconds(5);

    private readonly object _frameGate = new();
    private readonly SemaphoreSlim _frameOpenGate = new(1, 1);
    private readonly List<string> _frameResources = [];
    private FrameFeed? _frameFeed;
    private bool _frameOpened;
    private bool _frameStale;
    private long _frameNextAttempt;
    private string _frameOpenError = "";
    private long _frameExpected;

    /// <summary>The decoders owned by sibling groups the frame path calls (see <see cref="IFrameSections"/>); the lead binds them.</summary>
    internal IFrameSections? FrameSections { get; init; }

    /// <summary>
    /// Go's <c>caller.timeout</c> for one frame read: how long a read waits for a frame that answers it before it is
    /// unavailable.
    /// </summary>
    internal TimeSpan FrameReadTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Go's <c>caller.frames != nil</c>: whether reads are served from the snapshot stream. Off, the routine, combat,
    /// planning-window and table reads are unavailable and the colony facts, status and entity lists are read live.
    /// </summary>
    internal bool UseFrames { get; init; } = true;

    private FrameFeed Feed => LazyInitializer.EnsureInitialized(ref _frameFeed, () => new FrameFeed(Facade.Frames, RequireFrameSections))!;

    private IFrameSections RequireFrameSections() =>
        FrameSections ?? throw new InvalidOperationException("FacadeNativeClient.FrameSections is not bound: the frame path needs the decoders of the other client groups.");

    /// <summary>
    /// Go's <c>FrameSubscribe</c> side of <c>frameSubscribe</c>: a read of a resource's sources grows the subscription,
    /// and the next frame read resubscribes so later frames carry it.
    /// </summary>
    internal void FrameSubscribeResource(string resource)
    {
        if (!BridgeFunctions.IsValidId(resource))
        {
            return;
        }
        lock (_frameGate)
        {
            if (!_frameResources.Contains(resource))
            {
                _frameResources.Add(resource);
                _frameStale = true;
            }
        }
    }

    /// <summary>
    /// Go's <c>noteFrameWrite</c>: records that an applied write returned, so later frame reads wait for a frame
    /// captured past it. Go read the live ring's write counter, which the brain cannot see here; the next read must
    /// instead see a frame whose <see cref="NativeFrame.Writes"/> is at least one past the newest frame known now.
    /// Call it exactly once per write the mod applied (the mod bumps its counter once per applied write); a write the
    /// mod refused or skipped bumps nothing, and calling it for one only makes the next read wait out
    /// <see cref="FrameReadTimeout"/>.
    /// </summary>
    internal void NoteFrameWrite()
    {
        if (!UseFrames)
        {
            return;
        }
        var latest = Feed.Latest;
        lock (_frameGate)
        {
            if (!_frameOpened)
            {
                return;
            }
            _frameExpected = Math.Max(_frameExpected, latest?.Writes ?? 0) + 1;
        }
    }

    /// <summary>
    /// Go's <c>caller.openFrames</c> with the subscription it grows: subscribes in process to the native snapshot
    /// stream. <c>PlannerParts.OpenFrames</c> calls it once at start; a frame read calls it again by itself when the
    /// stream is not open or the subscription grew. The frames captured before it are dropped.
    /// </summary>
    internal async Task OpenFramesAsync(CancellationToken cancellationToken)
    {
        await _frameOpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] resources;
            lock (_frameGate)
            {
                resources = [.. _frameResources];
                _frameStale = false;
            }
            var request = new PbObservations.SnapshotStreamRequest
            {
                ResourceSources = [.. resources],
                RoutePathBudgetMs = RoutePathBudgetMs,
                HuntRouteBudgetMs = HuntRouteBudgetMs,
                HuntPredatorMarginCells = P.PolicyFunctions.HuntPredatorMarginCells,
                HerdRadius = HerdRadius,
                LinesOfFireBudgetMs = LinesOfFireBudgetMs,
            };
            PbObservations.SnapshotStreamReply reply;
            try
            {
                reply = await Facade.OpenFramesAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                lock (_frameGate)
                {
                    _frameStale = resources.Length != 0 || _frameStale;
                }
                throw;
            }
            var outcome = reply.Outcome;
            if (outcome?.Opened is null)
            {
                lock (_frameGate)
                {
                    _frameStale = true;
                }
                if (outcome?.Unavailable is { } unavailable)
                {
                    throw Unavailable(unavailable);
                }
                if (outcome?.Failure is { } failure)
                {
                    throw Failure(failure);
                }
                throw BridgeFunctions.Contract("snapshot stream outcome missing");
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
    /// Go <c>Client.FlushSnapshot</c> (<c>snapshot_flush.go</c>): asks native to capture the writes deferred so far.
    /// The reply does not wait for the capture.
    /// </summary>
    internal async Task FlushSnapshotAsync(CancellationToken cancellationToken)
    {
        var reply = await Facade.FlushFramesAsync(new PbObservations.FlushSnapshotRequest(), cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome?.Flushed is not null)
        {
            return;
        }
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        throw BridgeFunctions.Contract("flush snapshot outcome missing");
    }

    /// <summary>
    /// Go's <c>frameReader</c>: opens (or resubscribes) the stream when it is not open or the subscription grew,
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
    /// Go <c>frameReadView</c>: waits for the newest frame past this client's last write that answers the read
    /// (<paramref name="lookup"/>) and returns the answer. A frame carrying a Failure is a
    /// <see cref="Refusal"/> naming the section; a frame of another world is <see cref="BridgeRefusedException"/>;
    /// no frame within <see cref="FrameReadTimeout"/> is <see cref="BridgeUnavailableException"/>. With
    /// <paramref name="fallback"/>, a frame of the same world that carries the method only in another shape, while
    /// no subscription change is pending, is not served and the caller reads live. <paramref name="anyFrame"/> is
    /// Go's <c>WithAnyFrame</c>: the read skips the read-after-write wait.
    /// </summary>
    internal async Task<FrameServed<T>> FrameReadAsync<T>(
        string name,
        PbCommon.Identity identity,
        bool fallback,
        bool anyFrame,
        Func<FrameView, FrameLookup<T>> lookup,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(FrameReadTimeout);
        var token = limit.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await EnsureFramesAsync(token).ConfigureAwait(false);
                long expected;
                bool pending;
                lock (_frameGate)
                {
                    expected = anyFrame ? 0 : _frameExpected;
                    pending = _frameStale;
                }
                var frame = await Feed.LatestAsync(token).ConfigureAwait(false);
                long after = 0;
                if (frame is not null)
                {
                    after = frame.Number;
                    if (frame.Writes >= expected)
                    {
                        var prepared = frame.Prepared;
                        if (prepared.FailureDetail is not null)
                        {
                            throw new Refusal("snapshot_frame", prepared.FailureDetail, NoReceipt);
                        }
                        if (prepared.DecodeRefusal is { } decoded)
                        {
                            throw new Refusal(decoded.Tool, decoded.Cause, NoReceipt);
                        }
                        if (prepared.Problem is null)
                        {
                            var found = lookup(frame);
                            if (found.Found)
                            {
                                return new FrameServed<T>(true, found.Value);
                            }
                            if (prepared.World is not null && !BridgeFunctions.SameIdentity(prepared.World, identity))
                            {
                                throw new BridgeRefusedException($"{name} asked for another world than the snapshot frames describe");
                            }
                            if (prepared.World is not null && fallback && found.Carries && !pending)
                            {
                                return new FrameServed<T>(false, default);
                            }
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
            // A read that waited the whole timeout gives up its read-after-write wait: the write may never have
            // produced a counted frame (the counter here is an estimate), and every later read would stall too.
            var latest = Feed.Latest;
            lock (_frameGate)
            {
                _frameExpected = Math.Min(_frameExpected, latest?.Writes ?? 0);
            }
            string detail;
            lock (_frameGate)
            {
                detail = _frameOpened ? "no snapshot frame carried " + name : "snapshot stream not open for " + name + (_frameOpenError.Length == 0 ? "" : ": " + _frameOpenError);
            }
            throw new BridgeUnavailableException(detail);
        }
    }

    /// <summary>
    /// A frame read of one whole section: found when <paramref name="section"/> returns it, and a frame without it
    /// is waited past (Go: the frame then carries no key of the method, so the read waits for the next frame).
    /// </summary>
    internal async Task<T> FrameSectionAsync<T>(
        string name,
        PbCommon.Identity identity,
        bool anyFrame,
        Func<FrameView, T?> section,
        CancellationToken cancellationToken)
        where T : class
    {
        var served = await FrameReadAsync(
            name,
            identity,
            fallback: true,
            anyFrame,
            view =>
            {
                var value = section(view);
                return new FrameLookup<T>(value is not null, value, value is not null);
            },
            cancellationToken).ConfigureAwait(false);
        return served.Value!;
    }
}
