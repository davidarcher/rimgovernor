using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Runtime;

// ThingTable, ValidThing, FrameThings, ReadBuildings and ReadBillStacks.
public sealed partial class GameClient
{
    /// <summary>The methods naming the keyed tables a frame read waits for.</summary>
    private const string FramePawnsMethod = "snapshot_frame_pawns";

    private const string FrameThingsMethod = "snapshot_frame_things";

    private const string FrameBuildingsMethod = "snapshot_frame_buildings";

    private const string ReadBillsMethod = "observations_read_bills";

    /// <summary>
    /// The keyed tables of the newest frame (buildings, pawns, things) and its research census's finished projects: a
    /// frame that carries the pawn table carries the research section too.
    /// </summary>
    internal Task<Tables> FrameTablesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        FrameReadAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is null ? null : new Tables(
            view.Prepared.Held.Buildings,
            view.Prepared.Held.Pawns,
            view.Prepared.Held.Things,
            [.. (view.Snapshot.Research ?? throw BridgeContractException.Contract("frame with a pawn table and no research section")).Finished])
            {
                PawnCensus = view.Snapshot.Pawns, Emergency = view.Prepared.Emergency?.Facts,
                Rooms = view.Snapshot.Rooms ?? throw BridgeContractException.Contract("frame without its rooms section"),
                Zones = view.Snapshot.Zones ?? throw BridgeContractException.Contract("frame without its zones section"),
                RoomCells = RoomCellsOf(view.Snapshot.Rooms, view.Grid),
                ConstructionMemo = view.Prepared.Held.BuildMeta is not null ? _frameCensus
                    : throw BridgeContractException.Contract("frame with a pawn table and no building census"),
            }, cancellationToken);

    /// <summary>The pawn table of the newest frame.</summary>
    internal async Task<Pawns> FramePawnsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var held = await FrameHeldAsync(FramePawnsMethod, identity, view => view.Snapshot.Pawns is not null, cancellationToken).ConfigureAwait(false);
        return held.Pawns;
    }

    /// <summary>The things table of the newest frame.</summary>
    internal async Task<Things> FrameThingsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var held = await FrameHeldAsync(FrameThingsMethod, identity, view => view.Snapshot.Things is not null, cancellationToken).ConfigureAwait(false);
        return held.Things;
    }

    /// <summary>
    /// Every built, pending or blueprint player building (artificial) in one complete
    /// reply: the frame's held building table, with no encode round trip.
    /// </summary>
    internal async Task<EntityRows<RimGovernor.Brain.GameFacts.Observations.BuildingState>> ReadBuildingsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        WireValidation.AuthorityIdentity(identity);
        var held = await FrameHeldAsync(FrameBuildingsMethod, identity, view => view.Snapshot.Buildings is not null, cancellationToken).ConfigureAwait(false);
        return new EntityRows<RimGovernor.Brain.GameFacts.Observations.BuildingState> { Context = held.BuildMeta?.Context, Rows = held.Buildings.Table };
    }

    /// <summary>Every player bench's bill stack, keyed by bench id, from the frame's bills section.</summary>
    internal async Task<EntityRows<RimGovernor.Brain.GameFacts.Observations.BillStackRow>> ReadBillStacksAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        WireValidation.AuthorityIdentity(identity);
        var snapshot = await FrameReadAsync(ReadBillsMethod, identity, view => view.Snapshot.Bills, cancellationToken).ConfigureAwait(false);
        var table = default(Table<RimGovernor.Brain.GameFacts.Observations.BillStackRow>);
        foreach (var row in snapshot.Benches)
        {
            table = table.Set(row.Bench, row);
        }
        return new EntityRows<RimGovernor.Brain.GameFacts.Observations.BillStackRow> { Context = snapshot.Context, Rows = table };
    }

    /// <summary>
    /// The held keyed tables as of the newest frame past this client's last write that
    /// carries the table (<paramref name="carries"/>).
    /// </summary>
    internal Task<FrameHeld> FrameHeldAsync(string method, RimGovernor.Brain.GameFacts.Common.Identity identity, Func<FrameView, bool> carries, CancellationToken cancellationToken) =>
        FrameReadAsync(method, identity, view => carries(view) ? view.Prepared.Held : null, cancellationToken);
}
