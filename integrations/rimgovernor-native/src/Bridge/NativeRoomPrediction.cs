using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The planned room as it would stand once its ring pieces are built (#3339). Role is the game's own: every
/// RoomRoleDef.Worker.GetScore over an unregistered Room whose one region lists the observed things the closed room
/// would contain or touch (the role workers read only ContainedAndAdjacentThings). The four base stats are the
/// RoomStatWorker bodies of Wealth, Beauty, Space and Cleanliness run over the planned interior with the planned
/// pieces counted (Room.Cells needs a registered region grid, so they cannot run on the unregistered room); the
/// Impressiveness score is the game's worker over those stats and the stage RoomStatDef.GetScoreStageIndex. A piece
/// naming a TerrainDef is a planned floor (#3797): it replaces its room cell's terrain in the reported Cleanliness only,
/// not in the stats Impressiveness reads, and in the reported Terrains (the distinct interior terrains with their tags,
/// #3808). The planned furniture (#3840) stands in the closed room: counted once in Wealth, Beauty and Cleanliness, its
/// cells walkable only in Space unless it is standable, and an impassable piece's cells out of the room; the role
/// workers still list only observed things. The map-edge row reports the stats' roomlessScore. A shape with no passable
/// interior cell closes no room and has no row; a piece, stuff or game member that cannot be read throws (#3834). The
/// unregistered room is never added to the map's room lookup and touches no game state.
/// </summary>
internal static class NativeRoomPrediction
{
    // Room/District/Region private members the unregistered room needs (decompiled 1.7).
    private static readonly FieldInfo? RoomDistricts = BridgeCommon.PrivateInstanceField(typeof(Room), "districts");
    private static readonly FieldInfo? RoomStats = BridgeCommon.PrivateInstanceField(typeof(Room), "stats");
    private static readonly FieldInfo? RoomRole = BridgeCommon.PrivateInstanceField(typeof(Room), "role");
    private static readonly FieldInfo? RoomDirty = BridgeCommon.PrivateInstanceField(typeof(Room), "statsAndRoleDirty");
    private static readonly FieldInfo? DistrictRegions = BridgeCommon.PrivateInstanceField(typeof(District), "regions");
    private static readonly FieldInfo? DistrictRoom = BridgeCommon.PrivateInstanceField(typeof(District), "roomInt");
    private static readonly FieldInfo? DistrictEdge = BridgeCommon.PrivateInstanceField(typeof(District), "numRegionsTouchingMapEdge");
    private static readonly FieldInfo? RegionDistrict = BridgeCommon.PrivateInstanceField(typeof(Region), "districtInt");
    private static readonly FieldInfo? BeautyCellCurve = BridgeCommon.PrivateStaticField(typeof(RoomStatWorker_Beauty), "CellCountCurve");

    // Mirrored 1.7 literals (docs/developers/contracts/kept-constants.md, "Mirrored game literals"):
    // Room.PsychologicallyOutdoors (OpenRoofCountStopAt(300), open/cells >= 0.5 at the map edge),
    // Room.UpdateRoomStatsAndRole (MaxRegionsToAssignRoomRole 60), RoomStatWorker_Space (1.4 standable, 0.5 walkable,
    // cap 350; 350 when psychologically outdoors), BeautyUtility.CellBeauty (filth under a roof x0.3,
    // PollutedTerrainBeautyOffset -1).
    private const int PsychOutdoorsOpenRoof = 300;
    private const float PsychOutdoorsEdgeFraction = 0.5f;
    private const int MaxRegionsForRole = 60;
    private const float SpaceStandable = 1.4f, SpaceWalkable = 0.5f, SpaceCap = 350f;
    private const float RoofedFilthBeautyFactor = 0.3f, PollutedTerrainBeauty = -1f;

    /// <summary>One row per shape whose ring closes a room; a shape whose interior has no passable cell closes none and has no row.</summary>
    internal static EquatableArray<Obs.PlannedRoomPrediction> Predict(Map map, EquatableArray<Obs.PlannedRoomShape> shapes)
    {
        var rows = new List<Obs.PlannedRoomPrediction>();
        foreach (var shape in shapes)
            if (Predict(map, shape) is { } row) rows.Add(row);
        return EquatableArray.From(rows);
    }

    private sealed record Piece(IntVec3 Cell, ThingDef Def, ThingDef? Stuff);

    // A piece's stuff: none for a def made from none, else its own, else the shape's (a stuff-less ring site stands in every
    // ring stuff); throws when a stuffed def names no loaded stuff.
    private static ThingDef? StuffOf(ThingDef def, string own, Obs.PlannedRoomShape shape)
    {
        if (!def.MadeFromStuff) return null;
        var name = string.IsNullOrEmpty(own) ? shape.Stuff : own;
        return DefDatabase<ThingDef>.GetNamedSilentFail(name)
            ?? throw new InvalidOperationException($"planned room {shape.Room}: {def.defName} needs a stuff, got '{name}'");
    }

    private static Obs.PlannedRoomPrediction? Predict(Map map, Obs.PlannedRoomShape shape)
    {
        var pieces = new Dictionary<IntVec3, Piece>();
        var floors = new Dictionary<IntVec3, TerrainDef>();
        foreach (var p in shape.Pieces)
        {
            var cell = new IntVec3(p.Cell.X, 0, p.Cell.Z);
            if (!cell.InBounds(map)) throw new InvalidOperationException($"planned room {shape.Room}: piece {p.Definition} at {cell} is off the map");
            if (DefDatabase<ThingDef>.GetNamedSilentFail(p.Definition) is not { } def)
            {
                // A planned floor (a FloorIn site's terrain); a name that is neither def is no piece.
                floors[cell] = DefDatabase<TerrainDef>.GetNamedSilentFail(p.Definition)
                    ?? throw new InvalidOperationException($"planned room {shape.Room}: piece {p.Definition} names no ThingDef or TerrainDef");
                continue;
            }
            pieces[cell] = new(cell, def, StuffOf(def, p.Stuff, shape));
        }
        // The planned furniture (#3840), standing in the closed room: its occupied rect from its anchor, size and rotation.
        // An impassable piece's cells leave the room as a ring piece's do; a piece a pawn cannot stand on (a bed) makes its
        // cells walkable only, which Space counts at SpaceWalkable.
        var furniture = new List<Piece>();
        var furnitureBlocked = new HashSet<IntVec3>();
        var furnitureUnstandable = new HashSet<IntVec3>();
        var placed = new List<(ThingDef Def, IntVec3 Anchor, Rot4 Rot)>();
        foreach (var f in shape.Furniture)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(f.Definition)
                ?? throw new InvalidOperationException($"planned room {shape.Room}: furniture {f.Definition} names no ThingDef");
            var stuff = StuffOf(def, f.Stuff, shape);
            var anchor = new IntVec3(f.Cell.X, 0, f.Cell.Z);
            furniture.Add(new(anchor, def, stuff));
            placed.Add((def, anchor, new Rot4((int)f.Rotation)));
            foreach (var c in GenAdj.OccupiedRect(anchor, new Rot4((int)f.Rotation), def.size))
            {
                if (def.passability == Traversability.Impassable) furnitureBlocked.Add(c);
                else if (def.passability != Traversability.Standable) furnitureUnstandable.Add(c);
            }
        }
        // Room cells: the interior a region would cover (passable now; a planned piece cell is never interior).
        var cells = shape.Interior.Select(c => new IntVec3(c.X, 0, c.Z))
            .Where(c => c.InBounds(map) && !pieces.ContainsKey(c) && !furnitureBlocked.Contains(c) && !c.Impassable(map)).Distinct().ToList();
        var cellSet = new HashSet<IntVec3>(cells);
        var edge = cells.Any(c => c.OnEdge(map));
        var openRoof = cells.Count(c => !map.roofGrid.Roofed(c));
        var psychOutdoors = openRoof >= PsychOutdoorsOpenRoof || edge && cells.Count > 0 && (float)openRoof / cells.Count >= PsychOutdoorsEdgeFraction;
        // Region count once closed: regions never span a Region.GridSize block, so at least one per block covered.
        var regions = cells.Select(c => (c.x / Region.GridSize, c.z / Region.GridSize)).Distinct().Count();

        // ContainedAndAdjacentThings as RegionListersUpdater would list them: things on a room cell, and things whose
        // occupied rect expanded by one reaches a room cell they can touch (ReachabilityImmediate, Touch). Things on a
        // planned piece cell are cleared by its construction and left out.
        var contained = new List<Thing>();
        var seen = new HashSet<Thing>();
        foreach (var c in cells)
            foreach (var t in c.GetThingList(map))
                if (ListerThings.EverListable(t.def, ListerThingsUse.Region) && seen.Add(t)) contained.Add(t);
        var adjacent = new List<Thing>();
        foreach (var c in cells)
            foreach (var n in GenAdj.AdjacentCells.Select(o => c + o))
            {
                if (cellSet.Contains(n) || pieces.ContainsKey(n) || !n.InBounds(map)) continue;
                foreach (var t in n.GetThingList(map))
                {
                    if (seen.Contains(t) || !ListerThings.EverListable(t.def, ListerThingsUse.Region)) continue;
                    if (!t.OccupiedRect().ExpandedBy(1).Cells.Any(r => cellSet.Contains(r)
                        && ReachabilityImmediate.CanReachImmediate(r, t, map, PathEndMode.Touch, null))) continue;
                    seen.Add(t); adjacent.Add(t);
                }
            }
        // Planned pieces the room touches, as ReachabilityImmediate (Touch) lets the region lister list them: cardinally next
        // to a room cell, or diagonally when the piece can be touched through corners (ThingDef.CanInteractThroughCorners,
        // true of walls, so a ring corner counts) or a shared cardinal cell is walkable and no piece.
        bool Touches(Piece p) => GenAdj.CardinalDirections.Any(d => cellSet.Contains(p.Cell + d))
            || GenAdj.DiagonalDirections.Any(d => cellSet.Contains(p.Cell + d) && (p.Def.CanInteractThroughCorners
                || OpenSide(p.Cell + new IntVec3(d.x, 0, 0)) || OpenSide(p.Cell + new IntVec3(0, 0, d.z))));
        bool OpenSide(IntVec3 c) => c.InBounds(map) && !pieces.ContainsKey(c) && c.Walkable(map);
        var touching = pieces.Values.Where(Touches).ToList();
        var things = contained.Concat(adjacent).ToList();

        var beds = contained.OfType<Building_Bed>().Select(b => b.GetUniqueLoadID()).OrderBy(s => s, StringComparer.Ordinal);
        var benches = contained.Where(t => t is IBillGiver && t.def.building != null)
            .Where(_ => StatDefOf.WorkTableWorkSpeedFactor.parts?.Any(p => p is StatPart_WorkTableOutdoors) == true)
            .Select(t => new Obs.PlannedRoomBench(t.GetUniqueLoadID(), StatPart_WorkTableOutdoors.Applies(t)))
            .OrderBy(b => b.Bench, StringComparer.Ordinal);
        // The closed room's terrains with the planned floors laid (RoomRequirement_TerrainWithTags reads each cell's tags, #3808).
        var terrains = cells.Select(c => floors.TryGetValue(c, out var floor) ? floor : c.GetTerrain(map)).Distinct()
            .OrderBy(t => t.defName, StringComparer.Ordinal)
            .Select(t => new Obs.PlannedRoomTerrain(EquatableArray.From((t.tags ?? new List<string>()).OrderBy(s => s, StringComparer.Ordinal))))
            .ToList();
        var desks = PlannedDesks(map, placed);
        Obs.PlannedRoomPrediction Row(string role, float score, double clean) =>
            new(shape.Room, shape.Stuff, role, score, RoomStatDefOf.Impressiveness.GetScoreStageIndex(score), cells.Count, openRoof, edge)
                { Beds = EquatableArray.From(beds), Benches = EquatableArray.From(benches), Cleanliness = clean, Terrains = EquatableArray.From(terrains), Desks = desks };

        // No passable interior cell: closing the ring forms no room.
        if (cells.Count == 0) return null;
        // Room.ProperRoom and Room.UpdateRoomStatsAndRole: no role nor stats at the map edge or over 60 regions, where
        // Room.GetStat answers the stat's roomlessScore (and Room.GetStatScoreStage that score's stage).
        if (edge || regions > MaxRegionsForRole) return Row(RoomRoleDefOf.None.defName, RoomStatDefOf.Impressiveness.roomlessScore, RoomStatDefOf.Cleanliness.roomlessScore);
        var curve = BeautyCellCurve?.GetValue(null) as SimpleCurve
            ?? throw new InvalidOperationException("RoomStatWorker_Beauty.CellCountCurve is not readable");

        // The furniture counts as contained buildings: once each in Wealth and Cleanliness, and in Beauty once, on its cell
        // over the terrain (CellBeauty counts a thing once, at the first of its cells).
        var furnished = touching.Concat(furniture).ToList();
        var wealth = Wealth(map, cells, things, furnished);
        var beauty = Beauty(map, cells, cellSet, adjacent, touching, psychOutdoors, curve)
            + (cells.Count == 0 ? 0 : furniture.Sum(p => p.Def.GetStatValueAbstract(psychOutdoors && p.Def.StatBaseDefined(StatDefOf.BeautyOutdoors)
                ? StatDefOf.BeautyOutdoors : StatDefOf.Beauty, p.Stuff)) / curve.Evaluate(cells.Count));
        var space = psychOutdoors ? SpaceCap : Math.Min(cells.Sum(c => !furnitureUnstandable.Contains(c) && c.Standable(map) ? SpaceStandable
            : c.WalkableByNormal(map) ? SpaceWalkable : 0f), SpaceCap);
        var cleanliness = Cleanliness(map, cells, things, furnished, NoFloors);
        var floored = Cleanliness(map, cells, things, furnished, floors);

        var room = Unregistered(map, things, edge) ?? throw new InvalidOperationException("the unregistered Room/District/Region members are not readable");
        var stats = new DefMap<RoomStatDef, float>();
        stats[RoomStatDefOf.Wealth] = wealth; stats[RoomStatDefOf.Beauty] = beauty;
        stats[RoomStatDefOf.Space] = space; stats[RoomStatDefOf.Cleanliness] = cleanliness;
        (RoomStats ?? throw new InvalidOperationException("The room stats field is unavailable.")).SetValue(room, stats); (RoomDirty ?? throw new InvalidOperationException("The room dirty field is unavailable.")).SetValue(room, false);
        var role = DefDatabase<RoomRoleDef>.AllDefs.MaxBy(d => d.Worker.GetScore(room))
            ?? throw new InvalidOperationException("no RoomRoleDef is loaded");
        (RoomRole ?? throw new InvalidOperationException("The room role field is unavailable.")).SetValue(room, role);
        return Row(role.defName, RoomStatDefOf.Impressiveness.Worker.GetScore(room), floored);
    }

    private static readonly Dictionary<IntVec3, TerrainDef> NoFloors = new();

    /// <summary>
    /// The planned school desks among the shape's furniture (#3809), each at LearningUtility.SchoolDeskLearningRate's rate once
    /// built: 1 plus LearningRateBonusOffset_Blackboard per blackboard (the player's standing ones and the shape's planned ones) the
    /// desk's CompAffectedByFacilities could link (CanPotentiallyLinkTo_Static), at most MaxConnectedBlackboards. A blackboard
    /// needing line of sight (which only the comp's private check reads) is a broken read; the game's needs none.
    /// </summary>
    private static EquatableArray<Obs.PlannedRoomDesk> PlannedDesks(Map map, List<(ThingDef Def, IntVec3 Anchor, Rot4 Rot)> placed)
    {
        var desk = ThingDefOf.SchoolDesk;
        var board = ThingDefOf.Blackboard;
        if (desk == null || board == null || !placed.Any(p => p.Def == desk)) return [];
        var linkable = desk.GetCompProperties<CompProperties_AffectedByFacilities>()?.linkableFacilities?.Contains(board) == true;
        if (board.GetCompProperties<CompProperties_Facility>() is not { } facility) throw new InvalidOperationException("Blackboard has no CompProperties_Facility");
        if (facility.requiresLOS) throw new InvalidOperationException("Blackboard requires line of sight, which the desk prediction does not read");
        var boards = map.listerThings.ThingsOfDef(board).Where(t => t.Spawned && t.Faction == Faction.OfPlayer).Select(t => (t.Position, t.Rotation))
            .Concat(placed.Where(p => p.Def == board).Select(p => (p.Anchor, p.Rot))).ToList();
        return EquatableArray.From(placed.Where(p => p.Def == desk).Select(d =>
        {
            var linked = !linkable ? 0 : Math.Min(LearningUtility.MaxConnectedBlackboards,
                boards.Count(b => CompAffectedByFacilities.CanPotentiallyLinkTo_Static(board, b.Item1, b.Item2, desk, d.Anchor, d.Rot, map)));
            return new Obs.PlannedRoomDesk(1 + linked * LearningUtility.LearningRateBonusOffset_Blackboard);
        }));
    }

    // An unregistered Room -> District -> Region whose region lists the given things; null when a member was renamed.
    private static Room? Unregistered(Map map, List<Thing> things, bool edge)
    {
        if (RoomDistricts == null || RoomStats == null || RoomRole == null || RoomDirty == null || DistrictRegions == null
            || DistrictRoom == null || DistrictEdge == null || RegionDistrict == null) return null;
        var room = (Room)(Activator.CreateInstance(typeof(Room), true) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
        var district = (District)(Activator.CreateInstance(typeof(District), true) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
        var region = (Region)(Activator.CreateInstance(typeof(Region), true) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."));
        district.mapIndex = (sbyte)map.Index;
        region.mapIndex = (sbyte)map.Index;
        region.type = RegionType.Normal;
        region.touchesMapEdge = edge;
        ((List<Region>)(DistrictRegions.GetValue(district) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."))).Add(region);
        DistrictEdge.SetValue(district, edge ? 1 : 0);
        DistrictRoom.SetValue(district, room);
        RegionDistrict.SetValue(region, district);
        ((List<District>)(RoomDistricts.GetValue(room) ?? throw new System.InvalidOperationException("Required native reflection value is unavailable."))).Add(district);
        foreach (var t in things) region.ListerThings.Add(t);
        return room;
    }

    // RoomStatWorker_Wealth: buildings and plants contained or adjacent, plus every room cell's terrain.
    private static float Wealth(Map map, List<IntVec3> cells, List<Thing> things, List<Piece> pieces)
    {
        var sum = 0f;
        foreach (var t in things)
            if (t.def.category is ThingCategory.Building or ThingCategory.Plant) sum += t.stackCount * t.MarketValue;
        foreach (var p in pieces) sum += p.Def.GetStatValueAbstract(StatDefOf.MarketValue, p.Stuff);
        foreach (var c in cells) sum += c.GetTerrain(map).GetStatValueAbstract(StatDefOf.MarketValue);
        return sum;
    }

    // RoomStatWorker_Cleanliness: buildings, items, filth and plants contained or adjacent, plus terrain, per room cell; a
    // room cell with a planned floor counts that floor's terrain instead.
    private static float Cleanliness(Map map, List<IntVec3> cells, List<Thing> things, List<Piece> pieces, Dictionary<IntVec3, TerrainDef> floors)
    {
        var sum = 0f;
        foreach (var t in things)
            if (t.def.category is ThingCategory.Building or ThingCategory.Item or ThingCategory.Filth or ThingCategory.Plant)
                sum += t.stackCount * t.GetStatValue(StatDefOf.Cleanliness);
        foreach (var p in pieces) sum += p.Def.GetStatValueAbstract(StatDefOf.Cleanliness, p.Stuff);
        foreach (var c in cells) sum += (floors.TryGetValue(c, out var floor) ? floor : c.GetTerrain(map)).GetStatValueAbstract(StatDefOf.Cleanliness);
        return sum / cells.Count;
    }

    // RoomStatWorker_Beauty: CellBeauty over the room cells (judged with the closed room's PsychologicallyOutdoors), plus
    // once per position the cell beauty of each adjacent thing outside the room (BeautyUtility.CellBeauty as the game
    // reads it: a ring cell's room does not change on closure), and each touching planned piece's cell, which then
    // holds only that piece; divided by the worker's CellCountCurve.
    private static float Beauty(Map map, List<IntVec3> cells, HashSet<IntVec3> cellSet, List<Thing> adjacent, List<Piece> pieces,
        bool outdoors, SimpleCurve curve)
    {
        var counted = new HashSet<Thing>();
        var sum = 0f;
        foreach (var c in cells) sum += CellBeauty(map, c, outdoors, counted);
        var positions = new HashSet<IntVec3>();
        foreach (var t in adjacent)
            if (!cellSet.Contains(t.Position) && positions.Add(t.Position)) sum += BeautyUtility.CellBeauty(t.Position, map, counted);
        foreach (var p in pieces)
        {
            if (!positions.Add(p.Cell)) continue;
            var piece = p.Def.GetStatValueAbstract(p.Def.StatBaseDefined(StatDefOf.BeautyOutdoors) ? StatDefOf.BeautyOutdoors : StatDefOf.Beauty, p.Stuff);
            sum += p.Def.Fillage == FillCategory.Full ? piece : piece + p.Cell.GetTerrain(map).GetStatValueAbstract(StatDefOf.Beauty);
        }
        return cells.Count == 0 ? 0 : sum / curve.Evaluate(cells.Count);
    }

    // BeautyUtility.CellBeauty with the cell's PsychologicallyOutdoors given (the closed room's, not the cell's room now).
    private static float CellBeauty(Map map, IntVec3 c, bool outdoors, HashSet<Thing> counted)
    {
        float partial = 0f, full = 0f;
        var anyFull = false;
        var roofed = map.roofGrid.Roofed(c);
        foreach (var thing in map.thingGrid.ThingsListAt(c))
        {
            if (!BeautyUtility.BeautyRelevant(thing.def.category) || !counted.Add(thing)) continue;
            var group = thing.GetSlotGroup();
            if (thing.def.EverHaulable && group != null && group.parent != thing && group.parent.IgnoreStoredThingsBeauty) continue;
            var beauty = thing.GetBeauty(outdoors);
            if (thing.def.filth != null && roofed) beauty *= RoofedFilthBeautyFactor;
            if (thing.def.Fillage == FillCategory.Full) { anyFull = true; full += beauty; }
            else partial += beauty;
        }
        if (anyFull) return full;
        var terrain = map.terrainGrid.TerrainAt(c);
        if (ModsConfig.BiotechActive && !terrain.BuildableByPlayer && c.IsPolluted(map)) partial += PollutedTerrainBeauty;
        return outdoors && terrain.StatBaseDefined(StatDefOf.BeautyOutdoors)
            ? partial + terrain.GetStatValueAbstract(StatDefOf.BeautyOutdoors)
            : partial + terrain.GetStatValueAbstract(StatDefOf.Beauty);
    }
}
