using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// Go pawn_table.go (FrameTables, FramePawns, framePawnSnapshot, frameHeld), thing_table.go (ThingTable, ValidThing,
// FrameThings), building_table.go (FrameBuildings) and entities.go (ReadZones, ReadBuildings, ReadBillStacks).
public sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>framePawnsMethod</c>, <c>frameThingsMethod</c>, <c>frameBuildingsMethod</c>: the keyed tables a frame read waits for.</summary>
    private const string FramePawnsMethod = "rimgovernor/snapshot_frame_pawns";

    private const string FrameThingsMethod = "rimgovernor/snapshot_frame_things";

    private const string FrameBuildingsMethod = "rimgovernor/snapshot_frame_buildings";

    private const string ListBuildingsMethod = "rimgovernor/observations_list_buildings";

    private const string ReadBillsMethod = "rimgovernor/observations_read_bills";

    /// <summary>
    /// Go <c>Client.FrameTables</c>: the keyed tables of the newest frame (buildings, pawns, things). They are served
    /// only by the snapshot stream; Go's list-read fallback for a client without one is not ported.
    /// </summary>
    internal async Task<Tables> FrameTablesAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        RequireFrames("the keyed tables");
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return new Tables(held.Buildings, held.Pawns, held.Things);
    }

    /// <summary>Go <c>Client.FramePawns</c>: the pawn table of the newest frame.</summary>
    internal async Task<Pawns> FramePawnsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        RequireFrames("the pawn table");
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return held.Pawns;
    }

    /// <summary>
    /// Go <c>Client.FrameThings</c>: the things table of the newest frame. There is no live read behind it: without a
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
    /// Go <c>Client.FrameBuildings</c>: the building table of the newest frame (the bundle's player building list), or
    /// that list read live when this client reads no frames.
    /// </summary>
    internal async Task<Buildings> FrameBuildingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        if (UseFrames)
        {
            var held = await FrameHeldAsync(FrameBuildingsMethod, identity, view => view.Snapshot.Buildings is not null, cancellationToken).ConfigureAwait(false);
            return held.Buildings;
        }
        var observed = await ListBuildingsLiveAsync(identity, cancellationToken).ConfigureAwait(false);
        return Buildings.FromSnapshot(observed);
    }

    /// <summary>
    /// Go <c>Client.ReadZones</c>: every zone on the map (bounds and settings, no cells, contents or filter) in one
    /// complete reply. Go asked the stream first, but the request shape (no contents, no filter) is never the shape the
    /// frame's zone section is seeded under, so the read always went live; it does here.
    /// </summary>
    internal async Task<EntityRows<PbObservations.ZoneState>> ReadZonesAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        var reply = await Facade.ListZonesAsync(ZonesListRequest(identity), cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var snapshot = outcome.Observed!;
        var ids = new List<string>(snapshot.Zones.Count);
        foreach (var row in snapshot.Zones)
        {
            ids.Add(row.Id ?? "");
        }
        return EntityRowsOf(snapshot.Context, snapshot.Zones, ids);
    }

    /// <summary>
    /// Go <c>Client.ReadBuildings</c>: every built, pending or blueprint player building (artificial) in one complete
    /// reply. With a stream the frame's held building table is the list, with no encode round trip.
    /// </summary>
    internal async Task<EntityRows<PbObservations.BuildingState>> ReadBuildingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        if (UseFrames)
        {
            var held = await FrameHeldAsync(FrameBuildingsMethod, identity, view => view.Snapshot.Buildings is not null, cancellationToken).ConfigureAwait(false);
            return new EntityRows<PbObservations.BuildingState> { Context = held.BuildMeta?.Context, Rows = held.Buildings.Table };
        }
        var snapshot = await ListBuildingsLiveAsync(identity, cancellationToken).ConfigureAwait(false);
        return EntityRowsOf(snapshot.Context, snapshot.Buildings, [.. snapshot.Buildings.Select(row => row.Building.Id)]);
    }

    /// <summary>
    /// Go <c>Client.ReadBillStacks</c>: every player bench's bill stack, keyed by bench id. With a stream the frame's
    /// bills section answers it (it is seeded under exactly this request).
    /// </summary>
    internal async Task<EntityRows<PbObservations.BillStackRow>> ReadBillStacksAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        PbObservations.BillsSnapshot snapshot;
        if (UseFrames)
        {
            snapshot = await FrameSectionAsync(ReadBillsMethod, identity, anyFrame: false, view => view.Snapshot.Bills, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Go <c>zonesListRequest</c>: the zone list request shared with the bundle's step families.</summary>
    internal static PbObservations.ListZonesRequest ZonesListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, IncludeFilter = false };

    /// <summary>Go <c>buildingsListRequest</c>.</summary>
    internal static PbObservations.ListBuildingsRequest BuildingsListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };

    /// <summary>Go <c>billsListRequest</c>.</summary>
    internal static PbObservations.BillsRequest BillsListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };

    /// <summary>
    /// Go <c>Client.frameHeld</c>: the held keyed tables as of the newest frame past this client's last write that
    /// carries the table (<paramref name="carries"/>).
    /// </summary>
    internal async Task<FrameHeld> FrameHeldAsync(string method, PbCommon.Identity identity, Func<FrameView, bool> carries, CancellationToken cancellationToken)
    {
        var served = await FrameReadAsync(
            method,
            identity,
            fallback: false,
            anyFrame: false,
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

    /// <summary>Go <c>readEntities</c>' fold: the rows keyed by their ids.</summary>
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
