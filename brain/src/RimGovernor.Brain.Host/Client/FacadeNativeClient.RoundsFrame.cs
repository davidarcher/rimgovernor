using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

// Go frames.go: ReadRoundsFrame, DecodeRoundsFrame/decodeRoundsFrame, podArrivals, podsPending, roundsPawnRows,
// roundsPawnIDs, ReadCombat, combatFrameHeld, censusPawns, primaryWeaponThings, DecodeCombat, combatRoom, validateCombat.
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>roundsFrameMethod</c>: keys a frame's routine census sections.</summary>
    private const string RoundsFrameMethod = "rimgovernor/snapshot_frame_routine";

    /// <summary>Go <c>maxCombatRoomCells</c>: a larger enclosure is not a combat room.</summary>
    private const int MaxCombatRoomCells = 1024;

    /// <summary>Go's <c>frameStream.census</c>: the derived building census the routine readers update frame to frame.</summary>
    private readonly Memo _frameCensus = new();

    /// <summary>
    /// Go <c>Client.ReadRoundsFrame</c>: decodes the routine census of the newest frame past this client's last write
    /// against the load's definition catalog. Without a stream it is unavailable.
    /// </summary>
    internal async Task<RoundsFrame> ReadRoundsFrameAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        if (!UseFrames)
        {
            throw new BridgeUnavailableException("the routine census is served only by the snapshot stream");
        }
        BridgeFunctions.ValidateIdentity(identity);
        var sections = RequireFrameSections();
        var catalog = await sections.DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        var view = await FrameSectionAsync(RoundsFrameMethod, identity, anyFrame: false, frame => frame, cancellationToken).ConfigureAwait(false);
        var held = view.Prepared.Held;
        if (held.Error is { } problem)
        {
            throw problem;
        }
        var routine = DecodeRoundsFrame(view.Snapshot, catalog, held, sections);
        if (routine.Buildings is { } census)
        {
            routine = routine with { Buildings = census with { Memo = _frameCensus } };
        }
        var grid = view.Grid.Grid;
        if (grid is not null && BridgeFunctions.SameIdentity(view.Snapshot.Context?.Identity, identity))
        {
            routine = routine with { RoomCells = RoomCellsOf(routine.Rooms, grid) };
        }
        return routine;
    }

    /// <summary>Go <c>decodeRoundsFrame</c>: decodes the frame's routine sections; the keyed tables are <paramref name="held"/>'s, never the frame's lists.</summary>
    internal static RoundsFrame DecodeRoundsFrame(PbObservations.BundleSnapshot v, DefinitionCatalog catalog, FrameHeld held, IFrameSections sections)
    {
        var identity = v.Context!.Identity!;
        var pawns = held.Pawns;
        var output = new RoundsFrame
        {
            Context = v.Context,
            Colony = v.ColonyFacts,
            Rooms = v.Rooms,
            Bills = v.Bills,
            Buildings = held.BuildMeta is null ? null : new BuildingCensus(held.BuildMeta.Context, held.Buildings, held.BuildError as BridgeContractException),
            Tables = new Tables(held.Buildings, pawns, held.Things),
        };
        if (v.Emergency is not null)
        {
            var emergency = PodsPending(sections.DecodeEmergencyStatus(v.Emergency, pawns, identity), v.CombatEvents);
            output = output with { Emergency = emergency };
            if (held.PawnMeta is { } meta && RoundsPawnRows(meta, pawns, emergency) is { } colonists)
            {
                output = output with { Pawns = colonists };
            }
        }
        if (v.Population is not null)
        {
            output = output with { Population = sections.DecodePopulation(v.Population, pawns, catalog) };
        }
        if (v.Research is not null)
        {
            output = output with { Research = sections.DecodeResearch(v.Research, identity, catalog) };
        }
        if (v.Traders is not null)
        {
            output = output with { Traders = sections.DecodeTraders(v.Traders, identity) };
        }
        if (v.WorldProgression is not null)
        {
            output = output with { Quests = sections.DecodeWorldProgression(v.WorldProgression, identity) };
        }
        if (v.Ideology is not null)
        {
            output = output with { Ideology = sections.DecodeIdeology(v.Ideology, identity, catalog) };
            if (v.IdeologyActive is false)
            {
                throw BridgeFunctions.Contract("ideology section without the Ideology expansion");
            }
        }
        if (v.IdeologyActive is { } active)
        {
            output = output with { IdeologyActive = Fact.Known(active) };
        }
        if (v.Zones is not null)
        {
            output = output with { Zones = sections.DecodeZones(v.Zones, identity) };
        }
        return output;
    }

    /// <summary>Go <c>podArrivals</c>: the drop-pod arrival rows among a frame's combat events.</summary>
    internal static EquatableArray<PbMirror.CombatEventRow> PodArrivals(EquatableArray<PbMirror.CombatEventRow> events)
    {
        var output = new List<PbMirror.CombatEventRow>();
        foreach (var row in events)
        {
            if (BridgeFunctions.DropPodArrival(row))
            {
                output.Add(row);
            }
        }
        return [.. output];
    }

    /// <summary>
    /// Go <c>podsPending</c>: the emergency observation with <c>PodsOpen</c> set to the latest open tick of a drop-pod
    /// arrival among <paramref name="events"/> whose pods are still closed at the census tick: its raiders are in
    /// their pods, not in the census.
    /// </summary>
    internal static EmergencyObservation PodsPending(EmergencyObservation emergency, EquatableArray<PbMirror.CombatEventRow> events)
    {
        var censusTick = emergency.Context?.Tick ?? 0;
        var latest = emergency.Facts.PodsOpen;
        foreach (var row in events)
        {
            long open = row.OpenTick.GetValueOrDefault();
            if (BridgeFunctions.DropPodArrival(row) && open >= censusTick && new Tick(open) > latest)
            {
                latest = new Tick(open);
            }
        }
        return emergency with { Facts = emergency.Facts with { PodsOpen = latest } };
    }

    /// <summary>
    /// Go <c>roundsPawnRows</c>: the routine list read answered from <paramref name="table"/>'s envelope and the rows
    /// the census colonists resolve against; null without a complete census or when the table misses one.
    /// </summary>
    internal static PbObservations.PawnSnapshot? RoundsPawnRows(PbObservations.PawnSnapshot table, Pawns rows, EmergencyObservation emergency)
    {
        var ids = RoundsPawnIds(emergency);
        if (ids.Count == 0 || rows.Snapshot(table.Context!, ids) is not { } output)
        {
            return null;
        }
        return output with { MeditateAssignmentAvailable = table.MeditateAssignmentAvailable };
    }

    /// <summary>
    /// Go <c>roundsPawnIDs</c>: the colonists the routine census reads pawn detail for, in census order: every
    /// colonist of a complete emergency census, none otherwise.
    /// </summary>
    internal static List<string> RoundsPawnIds(EmergencyObservation emergency)
    {
        var ids = new List<string>();
        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete)
        {
            return ids;
        }
        foreach (var pawn in emergency.Facts.Colonists)
        {
            ids.Add(pawn.Id.Value);
        }
        return ids;
    }

    /// <summary>
    /// Go <c>Client.ReadCombat</c>: the combat state from the newest frame past this client's last write. There is no
    /// live read behind it: without a stream it is unavailable.
    /// </summary>
    internal async Task<Combat> ReadCombatAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        if (!UseFrames)
        {
            throw new BridgeUnavailableException("combat state is served only by the snapshot stream");
        }
        if (identity is null)
        {
            throw BridgeFunctions.Contract("combat read without an identity");
        }
        var sections = RequireFrameSections();
        var view = await FrameSectionAsync(CombatFrameMethod, identity, anyFrame: false, frame => frame, cancellationToken).ConfigureAwait(false);
        var combat = DecodeCombat(CombatFrame(view), sections);
        // The weapons' facts, the mech guard orders and a fight's race flags resolve against the catalog of the load.
        var catalog = await sections.DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        // An unbuildable race table fails the read here, so the fight's race lookups never see the error.
        _ = catalog.AnimalRaces();
        return combat with { Catalog = catalog, Shells = catalog.MortarShells(P.PolicyFunctions.MortarSafeRadius) };
    }

    /// <summary>Go <c>combatFrameHeld</c>: the part of the frame a combat read answers, its census rows looked up in the frame's held pawn table.</summary>
    private static PbObservations.BundleSnapshot CombatFrame(FrameView view)
    {
        var v = view.Snapshot;
        var pawns = CensusPawns(view);
        var frame = new PbObservations.BundleSnapshot
        {
            Context = v.Context,
            Emergency = v.Emergency,
            Pawns = pawns,
            CombatPawns = v.CombatPawns,
            CombatEvents = v.CombatEvents,
            CombatLinesOfFire = v.CombatLinesOfFire,
            Rooms = v.Rooms,
            CombatDoors = v.CombatDoors,
            CombatMortars = v.CombatMortars,
            CombatHiveTemperatureC = v.CombatHiveTemperatureC,
            WorldProgression = v.WorldProgression,
        };
        if (v.ColonyFacts?.OutdoorTemperatureC is { } outdoor)
        {
            frame = frame with { ColonyFacts = new PbObservations.ColonyFactsSnapshot { OutdoorTemperatureC = outdoor } };
        }
        return frame with { Things = PrimaryWeaponThings(view, pawns) };
    }

    /// <summary>Go <c>primaryWeaponThings</c>: the things rows the census pawns' primary weapons name.</summary>
    private static PbObservations.ThingsSnapshot? PrimaryWeaponThings(FrameView view, PbObservations.PawnSnapshot? pawns)
    {
        if (pawns is null)
        {
            return null;
        }
        var held = view.Prepared.Held;
        var output = new PbObservations.ThingsSnapshot { Context = view.Snapshot.Context };
        var rows = new List<PbObservations.Thing>();
        foreach (var pawn in pawns.Pawns)
        {
            var id = pawn.Equipment?.PrimaryId ?? "";
            if (id.Length != 0 && held.Things.TryGetValue(id, out var row) && row is not null)
            {
                rows.Add(row);
            }
        }
        return output with { Things = [.. rows] };
    }

    /// <summary>Go <c>censusPawns</c>: the frame's pawn table rows the emergency census references.</summary>
    private static PbObservations.PawnSnapshot? CensusPawns(FrameView view)
    {
        var v = view.Snapshot;
        var held = view.Prepared.Held;
        if (v.Pawns is null || v.Emergency is null || held.PawnMeta is null)
        {
            return null;
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
        // A squad hunt's prey: the open hunt rows of the colony census.
        if (v.ColonyFacts is { } colony)
        {
            foreach (var row in colony.Acquisition)
            {
                if (row.Hunt.GetValueOrDefault() && !row.Designated.GetValueOrDefault() && !row.Taken.GetValueOrDefault())
                {
                    ids.Add(row.Source?.Id ?? "");
                }
            }
        }
        // A living mechanitor's or mech's row rides with the census: the guard orders read the mechs from the combat frame.
        foreach (var row in held.Pawns.Table.Sorted())
        {
            if (row is not null && IsMechRow(row))
            {
                ids.Add(row.Pawn?.Id ?? "");
            }
        }
        var output = new List<PbObservations.PawnState>();
        foreach (var id in ids)
        {
            if (held.Pawns.TryGetValue(id, out var row) && row is not null)
            {
                output.Add(row);
            }
        }
        return new PbObservations.PawnSnapshot
        {
            Context = v.Pawns.Context,
            Completeness = v.Pawns.Completeness,
            MeditateAssignmentAvailable = v.Pawns.MeditateAssignmentAvailable,
            Pawns = [.. output],
        };
    }

    /// <summary>Go <c>isMechRow</c>: a living pawn carrying a mechanitor or mech block.</summary>
    private static bool IsMechRow(PbObservations.PawnState row) =>
        !row.Dead.GetValueOrDefault() && (row.Biotech?.Mechanitor is not null || row.Biotech?.Mech is not null);

    /// <summary>Go <c>DecodeCombat</c>: validates and decodes a frame's combat part.</summary>
    private Combat DecodeCombat(PbObservations.BundleSnapshot v, IFrameSections sections)
    {
        try
        {
            BridgeFunctions.ValidateContext(v.Context);
        }
        catch (BridgeContractException)
        {
            throw BridgeFunctions.Contract("combat frame without a context");
        }
        ValidateCombat(v);
        var identity = v.Context!.Identity!;
        var output = new Combat
        {
            Context = v.Context,
            Pawns = v.CombatPawns,
            Events = v.CombatEvents,
            Doors = v.CombatDoors,
            Frame = v,
        };
        if (v.WorldProgression is not null)
        {
            output = output with { World = sections.DecodeWorldProgression(v.WorldProgression, identity) };
        }
        var pawns = sections.PawnTable(v.Pawns, identity);
        output = output with { Detail = pawns, Things = ThingTable(v.Things, identity) };
        if (v.Emergency is not null)
        {
            output = output with { Emergency = PodsPending(sections.DecodeEmergencyStatus(v.Emergency, pawns, identity), v.CombatEvents) };
        }
        if (v.CombatLinesOfFire is { } lines)
        {
            var firing = new List<Cell>();
            var approach = new List<Cell>();
            var seen = new Dictionary<Cell, int>();
            foreach (var row in lines.Lines)
            {
                _ = TryWireCell(row.From, out var from);
                _ = TryWireCell(row.To, out var to);
                var flags = seen.GetValueOrDefault(from);
                if ((flags & 1) == 0)
                {
                    seen[from] = flags | 1;
                    firing.Add(from);
                }
                flags = seen.GetValueOrDefault(to);
                if ((flags & 2) == 0)
                {
                    seen[to] = flags | 2;
                    approach.Add(to);
                }
            }
            output = output with { Lines = sections.ValidateLinesOfFire(lines, identity, firing, approach), LinesSkipped = lines.Skipped.GetValueOrDefault() };
        }
        var mortars = new List<P.CombatMortar>();
        foreach (var row in v.CombatMortars)
        {
            _ = TryWireCell(row.Cell, out var cell);
            mortars.Add(new P.CombatMortar
            {
                Id = row.Id ?? "",
                Cell = cell,
                MinRange = row.MinRange.GetValueOrDefault(),
                MaxRange = row.MaxRange.GetValueOrDefault(),
                Loaded = row.LoadedShell ?? "",
            });
        }
        output = output with { Mortars = mortars };
        if (v.ColonyFacts?.OutdoorTemperatureC is { } outdoor)
        {
            output = output with { OutdoorTemperatureC = Fact.Known(outdoor) };
        }
        if (v.CombatHiveTemperatureC is { } hive)
        {
            output = output with { HiveTemperatureC = Fact.Known((double)hive) };
        }
        var doorStates = new List<P.RoomDoor>();
        var rooms = new List<P.CombatRoom>();
        foreach (var row in v.Rooms?.Rooms ?? default)
        {
            foreach (var door in row.Doors)
            {
                doorStates.Add(BridgeFunctions.RoomDoorFacts(door));
            }
            if (CombatRoom(row) is { } room)
            {
                rooms.Add(room);
            }
        }
        output = output with { Rooms = rooms };
        if (v.Rooms is not null)
        {
            output = output with { DoorStates = Fact.Known(EquatableArray.From(doorStates)) };
        }
        return output;
    }

    /// <summary>
    /// Go <c>combatRoom</c>: a frame room census row as the pods tactic's room: a proper room of at most
    /// <see cref="MaxCombatRoomCells"/> whose cells fill its extents, with the doors in its boundary. A room of any
    /// other shape is left out. The role is the census row's role string; one the policy does not know is unknown.
    /// </summary>
    private static P.CombatRoom? CombatRoom(PbObservations.RoomState row)
    {
        var cellCount = row.CellCount.GetValueOrDefault();
        if (!row.ProperRoom.GetValueOrDefault() || row.Doorway.GetValueOrDefault() || cellCount == 0 || cellCount > MaxCombatRoomCells)
        {
            return null;
        }
        if (!TryWireCell(row.Extents?.Minimum, out var low) || !TryWireCell(row.Extents?.Maximum, out var high) || high.X < low.X || high.Z < low.Z)
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
            Roofed = row.OpenRoofCount is not null && row.OpenRoofCount.GetValueOrDefault() == 0,
        };
        if (row.Role is not null)
        {
            room = room with { Role = Wire.TryParse<P.RoomRole>(row.Role, out var role) ? Fact.Known(role) : Fact.Unknown<P.RoomRole>() };
        }
        if (row.Burning is { } burning)
        {
            room = room with { Burning = Fact.Known(burning) };
        }
        var doors = new List<Cell>();
        foreach (var door in row.Doors)
        {
            if (TryWireCell(door.Cell, out var cell))
            {
                doors.Add(cell);
            }
        }
        return room with { Doors = EquatableArray.From(doors) };
    }

    /// <summary>Go <c>validateCombat</c>: checks a frame's combat rows.</summary>
    private static void ValidateCombat(PbObservations.BundleSnapshot v)
    {
        if (v.ColonyFacts?.OutdoorTemperatureC is { } outdoor && (double.IsNaN(outdoor) || outdoor < -300 || outdoor > 300))
        {
            throw BridgeFunctions.Contract("combat outdoor temperature");
        }
        if (v.CombatHiveTemperatureC is { } hive && (float.IsNaN(hive) || hive < -300 || hive > 1000))
        {
            throw BridgeFunctions.Contract("combat hive temperature");
        }
        foreach (var row in v.CombatMortars)
        {
            if (!BridgeFunctions.IsValidId(row.Id ?? "") || !MovementCellValid(row.Cell) || row.MinRange is null || row.MaxRange is null || row.MinRange.GetValueOrDefault() < 0 || row.MaxRange.GetValueOrDefault() < row.MinRange.GetValueOrDefault())
            {
                throw BridgeFunctions.Contract("combat mortar without id, cell or range");
            }
        }
        foreach (var row in v.CombatDoors)
        {
            if (!BridgeFunctions.IsValidId(row.Id ?? "") || !MovementCellValid(row.Cell) || row.HitPoints is null || row.MaxHitPoints is null || row.HitPoints.GetValueOrDefault() < 0 || row.HitPoints.GetValueOrDefault() > row.MaxHitPoints.GetValueOrDefault())
            {
                throw BridgeFunctions.Contract("combat door without id, cell or hit points");
            }
        }
        foreach (var row in v.CombatPawns)
        {
            if (!BridgeFunctions.IsValidId(row.Id ?? "") || row.Side.GetValueOrDefault() == PbMirror.CombatSide.Unspecified || row.Cell is null)
            {
                throw BridgeFunctions.Contract("combat pawn without id, side or cell");
            }
            foreach (var number in new[] { row.Health, row.BleedRate, row.Pain, row.MoveSpeed, row.ShieldEnergy, row.MeleePower })
            {
                if (!BridgeFunctions.CombatNumber(number, true))
                {
                    throw BridgeFunctions.Contract("combat pawn number");
                }
            }
            if (row.Health.GetValueOrDefault() > 1 || row.ShieldEnergy.GetValueOrDefault() > 1)
            {
                throw BridgeFunctions.Contract("combat pawn fraction");
            }
        }
        PbMirror.CombatEventRow? previous = null;
        foreach (var row in v.CombatEvents)
        {
            if (row.At is null || row.Kind.GetValueOrDefault() == PbMirror.CombatLogKind.Unspecified)
            {
                throw BridgeFunctions.Contract("combat event without watermark or kind");
            }
            if (BridgeFunctions.DropPodArrival(row) && (row.OpenTick.GetValueOrDefault() <= row.At.Tick.GetValueOrDefault() || row.LandingCells.Count == 0))
            {
                throw BridgeFunctions.Contract("drop-pod arrival without an open tick after it or landing cells");
            }
            if (previous is not null && !BridgeFunctions.CombatBefore(previous.At, row.At))
            {
                throw BridgeFunctions.Contract("combat events out of order");
            }
            previous = row;
        }
    }

    /// <summary>Go <c>movementCell</c>: a present, non-negative cell.</summary>
    private static bool MovementCellValid(PbCommon.Cell? cell) =>
        cell is { X: { } x, Z: { } z } && x >= 0 && z >= 0;

    /// <summary>Go <c>protoCell</c>: the cell of a wire cell, false when it is absent or lacks a coordinate (the cell is then the zero cell).</summary>
    private static bool TryWireCell(PbCommon.Cell? cell, out Cell result)
    {
        if (cell is { X: { } x, Z: { } z })
        {
            result = new Cell(x, z);
            return true;
        }
        result = default;
        return false;
    }
}
