using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

// ReadReviewFrame, DecodeReviewFrame/decodeReviewFrame, podsPending, reviewPawnRows,
// reviewPawnIDs, ReadCombat, combatFrameHeld, censusPawns, primaryWeaponThings, DecodeCombat, combatRoom.
public sealed partial class GameClient
{
    /// <summary>Keys a frame's routine census sections.</summary>
    private const string FrameMethod = "snapshot_frame_routine";

    /// <summary>A larger enclosure is not a combat room.</summary>
    private const int MaxCombatRoomCells = 1024;

    /// <summary>The derived building census the routine readers update frame to frame.</summary>
    private readonly Memo _frameCensus = new();

    /// <summary>
    /// Decodes the routine census of the newest frame past this client's last write
    /// against the load's definition catalog.
    /// </summary>
    internal async Task<ReviewFrame> ReadReviewFrameAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var catalog = await DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        var view = await FrameReadAsync(FrameMethod, identity, frame => frame, cancellationToken).ConfigureAwait(false);
        var routine = DecodeReviewFrame(view.Snapshot, catalog, view.Prepared);
        routine = routine with { Tables = routine.Tables with { ConstructionMemo = _frameCensus } };
        var cells = RoomCellsOf(routine.Rooms, view.Grid);
        routine = routine with { RoomCells = cells, Tables = routine.Tables with { RoomCells = cells } };
        return routine;
    }

    /// <summary>
    /// Decodes the frame's routine sections; the keyed tables and the emergency census
    /// are <paramref name="prepared"/>'s, decoded once per frame, never the frame's lists.
    /// </summary>
    internal static ReviewFrame DecodeReviewFrame(RimGovernor.Brain.GameFacts.Observations.BundleSnapshot v, DefinitionCatalog catalog, FramePrepared prepared)
    {
        var identity = v.Context!.Identity!;
        var held = prepared.Held;
        var pawns = held.Pawns;
        var pawnMeta = held.PawnMeta ?? throw BridgeContractException.Contract("routine frame without its pawn census" );
        var research = v.Research ?? throw BridgeContractException.Contract("routine frame without its research section");
        var output = new ReviewFrame
        {
            Context = v.Context,
            Colony = v.ColonyFacts,
            Rooms = v.Rooms ?? throw BridgeContractException.Contract("frame without its rooms section"),
            Bills = v.Bills,
            Buildings = held.BuildMeta is null ? null : new BuildingCensus(held.BuildMeta.Context, held.Buildings),
            Tables = new Tables(held.Buildings, pawns, held.Things, [.. research.Finished]) { PawnCensus = v.Pawns, Emergency = prepared.Emergency?.Facts, Rooms = v.Rooms ?? throw BridgeContractException.Contract("routine frame without its rooms section"), Zones = v.Zones ?? throw BridgeContractException.Contract("routine frame without its zones section") },
            Research = research,
        };
        if (prepared.Emergency is { } emergency)
        {
            output = output with { Emergency = emergency };
            if (PawnRows(pawnMeta, pawns, emergency) is { } colonists)
            {
                output = output with { Pawns = colonists };
            }
        }
        if (v.Population is not null)
        {
            output = output with { Population = PopulationDecoder.Decode(v.Population, pawns, catalog) };
        }
        if (v.Traders is not null)
        {
            output = output with { Traders = v.Traders };
        }
        if (v.WorldProgression is not null)
        {
            output = output with { Quests = v.WorldProgression };
        }
        return output with { Ideology = IdeologyDecoder.Mode(v.Ideology, catalog), Zones = v.Zones };
    }

    /// <summary>
    /// The emergency observation with <c>PodsOpen</c> set to the latest open tick of a drop-pod
    /// arrival among <paramref name="events"/> whose pods are still closed at the census tick: its raiders are in
    /// their pods, not in the census.
    /// </summary>
    internal static EmergencyObservation PodsPending(EmergencyObservation emergency, EquatableArray<RimGovernor.Brain.GameFacts.Mirror.CombatEventRow> events)
    {
        var censusTick = emergency.Context?.Tick ?? 0;
        var latest = emergency.Facts.PodsOpen;
        foreach (var row in events)
        {
            if (CombatCalls.DropPodArrival(row) && row.Pods is { OpenTick: var open } && open >= censusTick && new Tick(open) > latest)
            {
                latest = new Tick(open);
            }
        }
        return emergency with { Facts = emergency.Facts with { PodsOpen = latest } };
    }

    /// <summary>
    /// The routine list read answered from <paramref name="table"/>'s envelope and the rows
    /// the census colonists resolve against; null without a complete census or when the table misses one.
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.PawnSnapshot PawnRows(RimGovernor.Brain.GameFacts.Observations.PawnSnapshot table, Pawns rows, EmergencyObservation emergency)
    {
        var ids = PawnIds(emergency);
        if (rows.Snapshot(table.Context, ids) is not { } output)
        {
            throw BridgeContractException.Contract("routine pawn table misses a census colonist" );
        }
        return output;
    }

    /// <summary>
    /// The colonists the routine census reads pawn detail for, in census order: every
    /// colonist of a complete emergency census, none otherwise.
    /// </summary>
    internal static List<string> PawnIds(EmergencyObservation emergency)
    {
        var ids = new List<string>();
        foreach (var pawn in emergency.Facts.Colonists)
        {
            ids.Add(pawn.Id.Value);
        }
        return ids;
    }

    /// <summary>
    /// The combat state from the newest frame past this client's last write.
    /// </summary>
    internal async Task<Combat> ReadCombatAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        if (identity is null)
        {
            throw BridgeContractException.Contract("combat read without an identity");
        }
        var view = await FrameReadAsync(CombatFrameMethod, identity, frame => frame, cancellationToken).ConfigureAwait(false);
        var combat = DecodeCombat(view);
        // The weapons' facts, the mech guard orders and a fight's race flags resolve against the catalog of the load.
        var catalog = await DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        // An unbuildable race table fails the read here, so the fight's race lookups never see the error.
        _ = catalog.AnimalRaces();
        return combat with { Catalog = catalog, Shells = catalog.MortarShells(P.CombatMortarRules.MortarSafeRadius) };
    }

    /// <summary>The part of the frame a combat read answers, its census rows looked up in the frame's held pawn table.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.BundleSnapshot CombatFrame(FrameView view, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? pawns, RimGovernor.Brain.GameFacts.Observations.ThingsSnapshot? things)
    {
        var v = view.Snapshot;
        return new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot
        {
            Context = v.Context,
            Emergency = v.Emergency,
            Pawns = pawns,
            CombatPawns = v.CombatPawns,
            CombatEvents = v.CombatEvents,
            CombatLinesOfFire = v.CombatLinesOfFire,
            Rooms = v.Rooms ?? throw BridgeContractException.Contract("frame without its rooms section"),
            CombatDoors = v.CombatDoors,
            CombatMortars = v.CombatMortars,
            WorldProgression = v.WorldProgression,
            Things = things,
        };
    }

    /// <summary>
    /// The things rows the census pawns' primary weapons name, as the frame's list and
    /// keyed from the frame's held things table.
    /// </summary>
    private static (RimGovernor.Brain.GameFacts.Observations.ThingsSnapshot? Rows, Things Table) PrimaryWeaponThings(FrameView view, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? pawns)
    {
        if (pawns is null)
        {
            return (null, default);
        }
        var held = view.Prepared.Held;
        var rows = new List<RimGovernor.Brain.GameFacts.Observations.Thing>();
        var table = default(Table<RimGovernor.Brain.GameFacts.Observations.Thing>);
        foreach (var pawn in pawns.Pawns)
        {
            var id = pawn.Equipment.PrimaryId ?? "";
            if (id.Length != 0 && held.Things.TryGetValue(id, out var row) && row is not null)
            {
                rows.Add(row);
                table = table.Set(id, row);
            }
        }
        return (new RimGovernor.Brain.GameFacts.Observations.ThingsSnapshot { Things = [.. rows] }, new Things(table));
    }

    /// <summary>
    /// The frame's pawn table rows the emergency census references, as the frame's list and
    /// keyed from the frame's held pawn table.
    /// </summary>
    private static (RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? Rows, Pawns Table) CensusPawns(FrameView view)
    {
        var v = view.Snapshot;
        var held = view.Prepared.Held;
        if (v.Pawns is null || v.Emergency is null || held.PawnMeta is null)
        {
            return (null, default);
        }
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in v.Emergency.Colonists)
        {
            ids.Add(reference.Id ?? "");
        }
        if (v.Emergency.Threats is { } threats)
        {
            foreach (var row in threats.Pawns)
            {
                ids.Add(row.Pawn?.Id ?? "");
            }
        }
        // A squad hunt's prey: every hunt row of the colony census. A prey the squad already shoots at is taken (its
        // gunners' jobs target it), and it must stay in the read the hunt is ordered from.
        if (v.ColonyFacts is { } colony)
        {
            foreach (var row in colony.Acquisition)
            {
                if (row.Kind is RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt)
                {
                    ids.Add(row.Source.Id ?? "");
                }
            }
        }
        // A living mechanitor's or mech's row rides with the census: the guard orders read the mechs from the combat frame.
        foreach (var row in held.Pawns.Table.Sorted())
        {
            if (row is not null && IsMechRow(row))
            {
                ids.Add(row.Pawn.Id ?? "");
            }
        }
        // Every combat row's pawn rides with the census: its race, health and sapper facts are read off its row.
        foreach (var row in v.CombatPawns)
        {
            ids.Add(row.Id.Value);
        }
        var output = new List<RimGovernor.Brain.GameFacts.Observations.PawnState>();
        var table = default(Table<RimGovernor.Brain.GameFacts.Observations.PawnState>);
        foreach (var id in ids)
        {
            if (held.Pawns.TryGetValue(id, out var row) && row is not null)
            {
                output.Add(row);
                table = table.Set(id, row);
            }
        }
        var rows = new RimGovernor.Brain.GameFacts.Observations.PawnSnapshot
        {
            Context = v.Pawns.Context,
            Pawns = [.. output],
        };
        return (rows, new Pawns(table));
    }

    /// <summary>A living pawn carrying a mechanitor or mech block.</summary>
    private static bool IsMechRow(RimGovernor.Brain.GameFacts.Observations.PawnState row) =>
        !row.Dead && row.Biotech is RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active { Facts: var biotech } && (biotech.Mechanitor is not null || biotech.Mech is not null);

    /// <summary>
    /// Decodes a frame's combat part. The census pawns and weapons are keyed from
    /// the frame's held tables and the emergency census is the frame's, decoded once (<see cref="FrameView.Prepared"/>).
    /// </summary>
    internal static Combat DecodeCombat(FrameView view)
    {
        var (pawnRows, pawns) = CensusPawns(view);
        var (thingRows, things) = PrimaryWeaponThings(view, pawnRows);
        var v = CombatFrame(view, pawnRows, thingRows);
        var identity = v.Context!.Identity!;
        var output = new Combat
        {
            Context = v.Context,
            Pawns = v.CombatPawns,
            Events = v.CombatEvents,
            Doors = v.CombatDoors,
            Frame = v,
            Detail = pawns,
            Things = things,
        };
        if (v.WorldProgression is not null)
        {
            output = output with { World = v.WorldProgression };
        }
        if (view.Prepared.Emergency is { } emergency)
        {
            output = output with { Emergency = emergency };
        }
        if (v.CombatLinesOfFire is { } lines)
        {
            output = output with { Lines = lines.Lines, LinesSkipped = lines.Skipped };
        }
        var mortars = new List<P.CombatMortar>();
        foreach (var row in v.CombatMortars)
        {
            mortars.Add(new P.CombatMortar
            {
                Id = row.Id,
                Cell = new Cell(row.Cell.X, row.Cell.Z),
                MinRange = row.MinRange,
                MaxRange = row.MaxRange,
                Loaded = row.LoadedShell ?? "",
            });
        }
        output = output with { Mortars = mortars };
        // The cut frame carries no colony facts: the temperature is read off the whole frame, unknown when it has none.
        if (view.Snapshot.ColonyFacts is { } colony)
        {
            output = output with { OutdoorTemperature = new RimGovernor.Brain.Policy.OutdoorTemperature.Measured(colony.OutdoorTemperatureC) };
        }
        // Every frame carries its rooms census: the native capture fails the frame whole when the rooms read fails.
        var roomRows = v.Rooms ?? throw BridgeContractException.Contract("combat frame without its rooms section");
        var doorStates = new List<RimGovernor.Brain.GameFacts.Observations.RoomDoor>();
        var rooms = new List<P.CombatRoom>();
        foreach (var row in roomRows.Rooms)
        {
            foreach (var door in row.Doors)
            {
                doorStates.Add(door);
            }
            if (CombatRoom(row) is { } room)
            {
                rooms.Add(room);
            }
        }
        return output with { Rooms = rooms, DoorStates = EquatableArray.From(doorStates) };
    }

    /// <summary>
    /// A frame room census row as the pods tactic's room: a proper room of at most
    /// <see cref="MaxCombatRoomCells"/> whose cells fill its extents, with the doors in its boundary. A room of any
    /// other shape is left out.
    /// </summary>
    private static P.CombatRoom? CombatRoom(RimGovernor.Brain.GameFacts.Observations.RoomState row)
    {
        var cellCount = row.CellCount;
        if (!row.ProperRoom || row.Doorway || cellCount == 0 || cellCount > MaxCombatRoomCells)
        {
            return null;
        }
        var low = row.Extents.Minimum;
        var high = row.Extents.Maximum;
        if (high.X < low.X || high.Z < low.Z)
        {
            return null;
        }
        long width = high.X - low.X + 1;
        long height = high.Z - low.Z + 1;
        if (width * height != cellCount)
        {
            return null;
        }
        var room = new P.CombatRoom
        {
            Interior = new P.Rectangle(low.X, low.Z, (int)width, (int)height),
            Roofed = row.OpenRoofCount == 0,
            Role = row.Role,
            Burning = row.Burning,
            Doors = EquatableArray.From(row.Doors.Select(door => door.Cell)),
        };
        return room;
    }
}

