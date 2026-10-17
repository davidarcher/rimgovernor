using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// ThingTable, ValidThing, FrameThings, ReadBuildings and ReadBillStacks.
public sealed partial class FacadeNativeClient
{
    /// <summary>The methods naming the keyed tables a frame read waits for.</summary>
    private const string FramePawnsMethod = "rimgovernor/snapshot_frame_pawns";

    private const string FrameThingsMethod = "rimgovernor/snapshot_frame_things";

    private const string FrameBuildingsMethod = "rimgovernor/snapshot_frame_buildings";

    private const string ListBuildingsMethod = "rimgovernor/observations_list_buildings";

    private const string ReadBillsMethod = "rimgovernor/observations_read_bills";

    /// <summary>
    /// The keyed tables of the newest frame (buildings, pawns, things). They are served
    /// only by the snapshot stream.
    /// </summary>
    internal async Task<Tables> FrameTablesAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        RequireFrames("the keyed tables");
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return new Tables(held.Buildings, held.Pawns, held.Things);
    }

    /// <summary>The pawn table of the newest frame.</summary>
    internal async Task<Pawns> FramePawnsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        RequireFrames("the pawn table");
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return held.Pawns;
    }

    /// <summary>
    /// The things table of the newest frame. There is no live read behind it: without a
    /// stream the table is empty and every food stock reference waits.
    /// </summary>
    internal async Task<Things> FrameThingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        if (!UseFrames)
        {
            return default;
        }
        var held = await FrameHeldAsync(FrameThingsMethod, identity, view => view.Snapshot.Things is not null, cancellationToken).ConfigureAwait(false);
        return held.Things;
    }

    /// <summary>
    /// Every built, pending or blueprint player building (artificial) in one complete
    /// reply. With a stream the frame's held building table is the list, with no encode round trip.
    /// </summary>
    internal async Task<EntityRows<PbObservations.BuildingState>> ReadBuildingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        WireValidation.AuthorityIdentity(identity);
        if (UseFrames)
        {
            var held = await FrameHeldAsync(FrameBuildingsMethod, identity, view => view.Snapshot.Buildings is not null, cancellationToken).ConfigureAwait(false);
            return new EntityRows<PbObservations.BuildingState> { Context = held.BuildMeta?.Context, Rows = held.Buildings.Table };
        }
        var snapshot = await ListBuildingsLiveAsync(identity, cancellationToken).ConfigureAwait(false);
        return EntityRowsOf(snapshot.Context, snapshot.Buildings, [.. snapshot.Buildings.Select(row => row.Building.Id)]);
    }

    /// <summary>
    /// Every player bench's bill stack, keyed by bench id. With a stream the frame's
    /// bills section answers it (it is seeded under exactly this request).
    /// </summary>
    internal async Task<EntityRows<PbObservations.BillStackRow>> ReadBillStacksAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        WireValidation.AuthorityIdentity(identity);
        PbObservations.BillsSnapshot snapshot;
        if (UseFrames)
        {
            snapshot = await FrameSectionAsync(ReadBillsMethod, identity, view => view.Snapshot.Bills, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var reply = await Facade.ReadBillsAsync(BillsListRequest(identity), cancellationToken).ConfigureAwait(false);
            var outcome = reply.Outcome;
            if (outcome.Failure is { } failure)
            {
                throw Failure(failure);
            }
            if (outcome.Unavailable is { } unavailable)
            {
                throw Unavailable(unavailable);
            }
            snapshot = outcome.Observed!;
        }
        var ids = new List<string>(snapshot.Benches.Count);
        foreach (var row in snapshot.Benches)
        {
            ids.Add(row.Bench?.Id ?? "");
        }
        return EntityRowsOf(snapshot.Context, snapshot.Benches, ids);
    }

    internal static PbObservations.ListBuildingsRequest BuildingsListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };

    internal static PbObservations.BillsRequest BillsListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };

    /// <summary>
    /// The held keyed tables as of the newest frame past this client's last write that
    /// carries the table (<paramref name="carries"/>).
    /// </summary>
    internal async Task<FrameHeld> FrameHeldAsync(string method, PbCommon.Identity identity, Func<FrameView, bool> carries, CancellationToken cancellationToken)
    {
        var served = await FrameReadAsync(
            method,
            identity,
            fallback: false,
            view => new FrameLookup<FrameHeld>(carries(view), view.Prepared.Held, carries(view)),
            cancellationToken).ConfigureAwait(false);
        var held = served.Value!;
        return held;
    }

    private void RequireFrames(string what)
    {
        if (!UseFrames)
        {
            throw new BridgeUnavailableException(what + " is served only by the snapshot stream");
        }
    }

    private async Task<PbObservations.BuildingsSnapshot> ListBuildingsLiveAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var reply = await Facade.ListBuildingsAsync(BuildingsListRequest(identity), cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        return outcome.Observed!;
    }

    /// <summary>Folds the rows keyed by their ids.</summary>
    private static EntityRows<TRow> EntityRowsOf<TRow>(PbCommon.ObservationContext? context, IReadOnlyList<TRow> rows, List<string> ids)
    {
        var table = default(Table<TRow>);
        for (var i = 0; i < rows.Count; i++)
        {
            table = table.Set(ids[i], rows[i]);
        }
        return new EntityRows<TRow> { Context = context, Rows = table };
    }
}
