using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.Host;

// Go pawn_table.go (FrameTables, FramePawns, framePawnSnapshot, frameHeld), thing_table.go (ThingTable, ValidThing,
// FrameThings), building_table.go (FrameBuildings) and entities.go (ReadZones, ReadBuildings, ReadBillStacks).
internal sealed partial class FacadeNativeClient
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
        BridgeFunctions.ValidateIdentity(identity);
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return new Tables(held.Buildings, held.Pawns, held.Things);
    }

    /// <summary>Go <c>Client.FramePawns</c>: the pawn table of the newest frame.</summary>
    internal async Task<Pawns> FramePawnsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        RequireFrames("the pawn table");
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return held.Pawns;
    }

    /// <summary>Go <c>Client.framePawnSnapshot</c>: the pawn table as a list snapshot in id order.</summary>
    internal async Task<PbObservations.PawnSnapshot> FramePawnSnapshotAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
        RequireFrames("the pawn table");
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return held.PawnList() ?? throw BridgeFunctions.Contract("pawn table outcome missing");
    }

    /// <summary>
    /// Go <c>Client.FrameThings</c>: the things table of the newest frame. There is no live read behind it: without a
    /// stream the table is empty and every food stock reference waits.
    /// </summary>
    internal async Task<Things> FrameThingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.ValidateIdentity(identity);
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
        BridgeFunctions.ValidateIdentity(identity);
        if (UseFrames)
        {
            var held = await FrameHeldAsync(FrameBuildingsMethod, identity, view => view.Snapshot.Buildings is not null, cancellationToken).ConfigureAwait(false);
            return held.Buildings;
        }
        var observed = await ListBuildingsLiveAsync(identity, cancellationToken).ConfigureAwait(false);
        try
        {
            BridgeFunctions.ValidateContext(observed.Context);
        }
        catch (BridgeContractException)
        {
            throw BridgeFunctions.Contract("building table without the expected context");
        }
        if (!BridgeFunctions.SameIdentity(observed.Context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("building table without the expected context");
        }
        return Buildings.FromSnapshot(observed);
    }

    /// <summary>
    /// Go <c>Client.ReadZones</c>: every zone on the map (bounds and settings, no cells, contents or filter) in one
    /// complete reply. Go asked the stream first, but the request shape (no contents, no filter) is never the shape the
    /// frame's zone section is seeded under, so the read always went live; it does here.
    /// </summary>
    internal async Task<(EntityRows<PbObservations.ZoneState> Value, BridgeResult Receipt)> ReadZonesAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        var reply = await Facade.ListZonesAsync(ZonesListRequest(identity), cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome ?? throw BridgeFunctions.Contract("missing zones outcome");
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        var snapshot = outcome.Observed ?? throw BridgeFunctions.Contract("missing zones snapshot");
        var ids = new List<string>(snapshot.Zones.Count);
        foreach (var row in snapshot.Zones)
        {
            ids.Add((row ?? throw BridgeFunctions.Contract("invalid zone row")).Id ?? "");
        }
        return (EntityRowsOf(identity, snapshot.Context, snapshot.Zones, ids), NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadBuildings</c>: every built, pending or blueprint player building (artificial) in one complete
    /// reply. With a stream the frame's held building table is the list, with no encode round trip.
    /// </summary>
    internal async Task<(EntityRows<PbObservations.BuildingState> Value, BridgeResult Receipt)> ReadBuildingsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        if (UseFrames)
        {
            var held = await FrameHeldAsync(FrameBuildingsMethod, identity, view => view.Snapshot.Buildings is not null, cancellationToken).ConfigureAwait(false);
            if (held.BuildListError is { } listError)
            {
                throw listError;
            }
            return (new EntityRows<PbObservations.BuildingState> { Context = held.BuildMeta?.Context, Rows = held.Buildings.Table }, NoReceipt);
        }
        var snapshot = await ListBuildingsLiveAsync(identity, cancellationToken).ConfigureAwait(false);
        var ids = new List<string>(snapshot.Buildings.Count);
        foreach (var row in snapshot.Buildings)
        {
            if (row is null || row.Building is null)
            {
                throw BridgeFunctions.Contract("invalid building row");
            }
            ids.Add(row.Building.Id ?? "");
        }
        return (EntityRowsOf(identity, snapshot.Context, snapshot.Buildings, ids), NoReceipt);
    }

    /// <summary>
    /// Go <c>Client.ReadBillStacks</c>: every player bench's bill stack, keyed by bench id. With a stream the frame's
    /// bills section answers it (it is seeded under exactly this request).
    /// </summary>
    internal async Task<(EntityRows<PbObservations.BillStackRow> Value, BridgeResult Receipt)> ReadBillStacksAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
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
            var outcome = reply.Outcome ?? throw BridgeFunctions.Contract("missing bills outcome");
            if (outcome.Failure is { } failure)
            {
                throw Failure(failure);
            }
            if (outcome.Unavailable is { } unavailable)
            {
                throw Unavailable(unavailable);
            }
            snapshot = outcome.Observed ?? throw BridgeFunctions.Contract("missing bills snapshot");
        }
        var ids = new List<string>(snapshot.Benches.Count);
        foreach (var row in snapshot.Benches)
        {
            if (row is null || row.Bench is null)
            {
                throw BridgeFunctions.Contract("invalid bill stack row");
            }
            ids.Add(row.Bench.Id ?? "");
        }
        return (EntityRowsOf(identity, snapshot.Context, snapshot.Benches, ids), NoReceipt);
    }

    /// <summary>Go <c>zonesListRequest</c>: the zone list request shared with the bundle's step families.</summary>
    internal static PbObservations.ListZonesRequest ZonesListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, IncludeContents = false, IncludeFilter = false };

    /// <summary>Go <c>buildingsListRequest</c>.</summary>
    internal static PbObservations.ListBuildingsRequest BuildingsListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, PlayerOnly = true, Category = "artificial" };

    /// <summary>Go <c>billsListRequest</c>.</summary>
    internal static PbObservations.BillsRequest BillsListRequest(PbCommon.Identity identity) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };

    /// <summary>
    /// Go <c>ThingTable</c>: validates a things snapshot against the identity and indexes its rows by id; a null
    /// snapshot is an empty table.
    /// </summary>
    internal Things ThingTable(PbObservations.ThingsSnapshot? snapshot, PbCommon.Identity identity)
    {
        if (snapshot is null)
        {
            return default;
        }
        BridgeFunctions.ValidateContext(snapshot.Context);
        if (!BridgeFunctions.SameIdentity(snapshot.Context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("things table world mismatch");
        }
        var sections = RequireFrameSections();
        var table = default(Table<PbObservations.Thing>);
        foreach (var row in snapshot.Things)
        {
            CheckThingRow(row, snapshot.Context, sections);
            var id = row.Entity?.Id ?? "";
            if (table.Has(id))
            {
                throw BridgeFunctions.Contract("duplicate thing row");
            }
            table = table.Set(id, row);
        }
        return new Things(table);
    }

    /// <summary>
    /// Go <c>ValidThing</c>: checks one canonical thing row, its entity, counts and, for a corpse, finite facts
    /// consistent with its kind.
    /// </summary>
    internal static void CheckThingRow(PbObservations.Thing? row, PbCommon.ObservationContext context, IFrameSections sections)
    {
        if (row is null)
        {
            throw BridgeFunctions.Contract("thing row missing");
        }
        sections.CheckEntity(row.Entity, context);
        if ((row.StackCount is not null && row.StackCount.GetValueOrDefault() < 0)
            || (row.RotTicks is not null && row.RotTicks.GetValueOrDefault() < 0)
            || !BridgeFunctions.CombatNumber(row.TemperatureC, false))
        {
            throw BridgeFunctions.Contract("invalid thing facts");
        }
        if (!BridgeFunctions.OptionalRef(row.Room))
        {
            throw BridgeFunctions.Contract("invalid thing room");
        }
        if (row.Corpse.GetValueOrDefault())
        {
            if (row.Forbidden is null
                || !BridgeFunctions.CombatNumber(row.MeatAmount, true)
                || row.MeatAmount is null
                || row.BodySize is null
                || !BridgeFunctions.CombatNumber(row.BodySize, true)
                || row.BodySize.GetValueOrDefault() <= 0
                || row.TileFootprint.GetValueOrDefault() != 1
                || row.StackCount.GetValueOrDefault() != 1)
            {
                throw BridgeFunctions.Contract("invalid corpse row");
            }
        }
        else if (row.MeatAmount is not null || row.BodySize is not null || row.TileFootprint is not null)
        {
            throw BridgeFunctions.Contract("corpse facts on a thing that is no corpse");
        }
    }

    /// <summary>
    /// Go <c>Client.frameHeld</c>: the held keyed tables as of the newest frame past this client's last write that
    /// carries the table (<paramref name="carries"/>), failing with the first invalid pawn or thing row.
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
        if (held.Error is { } error)
        {
            throw error;
        }
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
        var outcome = reply.Outcome ?? throw BridgeFunctions.Contract("missing buildings outcome");
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        return outcome.Observed ?? throw BridgeFunctions.Contract("missing buildings snapshot");
    }

    /// <summary>Go <c>readEntities</c>' fold: the context checked against the identity, the rows keyed by their valid, unique ids.</summary>
    private static EntityRows<TRow> EntityRowsOf<TRow>(PbCommon.Identity identity, PbCommon.ObservationContext? context, IReadOnlyList<TRow> rows, List<string> ids)
    {
        BridgeFunctions.ValidateContext(context);
        if (!BridgeFunctions.SameIdentity(context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("entity list identity mismatch");
        }
        var table = default(Table<TRow>);
        for (var i = 0; i < rows.Count; i++)
        {
            var id = ids[i];
            if (!BridgeFunctions.IsValidId(id))
            {
                throw BridgeFunctions.Contract("invalid entity identity");
            }
            if (table.Has(id))
            {
                throw BridgeFunctions.Contract("duplicate entity");
            }
            table = table.Set(id, rows[i]);
        }
        return new EntityRows<TRow> { Context = context, Rows = table };
    }
}
