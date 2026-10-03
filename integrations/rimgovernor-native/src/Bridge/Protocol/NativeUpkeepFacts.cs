#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // Complete independent censuses: a failed section contributes an issue and
    // no rows. The enclosing colony boundary still enforces its one-MiB limit.
    internal static class NativeUpkeepFacts
    {
        // Every section reads one of four subsets of the visible spawned
        // things. One pass over the colony read's list fills all four
        // (#1296); each keeps the list's order, so every section's filter
        // and sort sees the same rows it saw when it scanned the list
        // itself. SharedPass off rebuilds the list and scans it per subset,
        // the pre-#1296 path the ColonyFacts equality probe compares against.
        internal static bool SharedPass = true;

        internal sealed class ThingSets
        {
            internal readonly List<Thing> Items = new List<Thing>();
            internal readonly List<Building> PlayerBuildings = new List<Building>();
            internal readonly List<Fire> Fires = new List<Fire>();
            internal readonly List<Filth> Filth = new List<Filth>();

            internal static ThingSets Of(Map map, List<Thing> things)
            {
                var sets = new ThingSets();
                var player = Faction.OfPlayerSilentFail;
                if (!SharedPass) {
                    things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
                    sets.Items.AddRange(things.Where(t => t.def.category == ThingCategory.Item));
                    sets.PlayerBuildings.AddRange(things.OfType<Building>().Where(b => b.Faction == player));
                    sets.Fires.AddRange(things.OfType<Fire>());
                    sets.Filth.AddRange(things.OfType<Filth>());
                    return sets;
                }
                foreach (var t in things) {
                    if (t.def.category == ThingCategory.Item) sets.Items.Add(t);
                    switch (t) {
                        case Building b when b.Faction == player: sets.PlayerBuildings.Add(b); break;
                        case Fire f: sets.Fires.Add(f); break;
                        case Filth f: sets.Filth.Add(f); break;
                    }
                }
                return sets;
            }
        }

        internal static void Populate(Map map, List<Thing> things, Obs.UpkeepFacts result)
        {
            var sets = ThingSets.Of(map, things);
            Read("items", result, () => {
                // Every real map carries hundreds of natural chunk and slag
                // stacks that no upkeep goal may ever target, and a whole-map
                // census exceeded the bound on every real map. Items count
                // when the home area holds them (MaintainStorage debris),
                // when they sit in storage (reserve stock wherever it is
                // kept), or when they are the deteriorating, perishable or
                // medicine stacks SecureSupplies and MaintainMedicalReserves
                // follow wherever they were dropped.
                // Corpses deteriorate and rot like food but are never
                // supplies to secure: a raider corpse across the map would
                // otherwise keep the SecureSupplies deficit open forever.
                // A settled stored stack (roofed, or not deteriorating) is no
                // target of any upkeep goal: only unstored rows and deteriorating
                // unroofed ones are selected. Medicine stays for the reserve count.
                var rows = sets.Items.Where(t => !t.def.IsCorpse
                    && (t.Faction == null || t.Faction == Faction.OfPlayerSilentFail)
                    && (map.areaManager.Home[t.Position] || t.IsInValidStorage() || t.def.IsMedicine
                        || t.def.GetStatValueAbstract(StatDefOf.DeteriorationRate, t.Stuff) > 0f
                        || (t.TryGetComp<CompRottable>()?.Active ?? false))
                    && !(t.IsInValidStorage() && !t.def.IsMedicine
                        && (t.Position.Roofed(map) || t.def.GetStatValueAbstract(StatDefOf.DeteriorationRate, t.Stuff) <= 0f)))
                    .OrderBy(t => t.thingIDNumber).ToList();
                var values = rows.Select(t => {
                    var rot = t.TryGetComp<CompRottable>();
                    var value = new Obs.UpkeepItem {
                        Item = NativeRef.Thing(t), Count = t.stackCount, Roofed = t.Position.Roofed(map), InStorage = t.IsInValidStorage(),
                        DeteriorationRate = Number(t.GetStatValue(StatDefOf.DeteriorationRate)),
                        Forbidden = t.IsForbidden(Faction.OfPlayerSilentFail)
                    };
                    if (rot != null && rot.Active) value.RotTicks = Math.Max(0, rot.TicksUntilRotAtCurrentTemp);
                    return value;
                }).ToList();
                result.Items.AddRange(values);
            });
            Read("structures", result, () => {
                // Non-damageable markers (including sleeping spots) have a
                // native -1 sentinel and cannot be repair targets.
                var rows = sets.PlayerBuildings.Where(b => b.def.useHitPoints).OrderBy(b => b.thingIDNumber).ToList();
                var values = rows.Select(b => new Obs.UpkeepStructure {
                    Building = NativeBuildingObservationTools.Ref(b),
                    Home = b.OccupiedRect().All(c => map.areaManager.Home[c]), Flammability = Number(b.GetStatValue(StatDefOf.Flammability)),
                    RepairPriority = b.TryGetComp<CompTempControl>() != null || b.TryGetComp<CompPowerPlant>() != null
                        || b is Building_Bed bed && bed.Medical ? 0
                        : b.def.holdsRoof || b is Building_WorkTable || b is Building_Bed ? 1 : 2
                }).ToList();
                result.Structures.AddRange(values);
            });
            Read("fires", result, () => {
                var rows = sets.Fires.OrderBy(f => f.thingIDNumber).ToList();
                var values = rows.Select(f => new Obs.FireState { Fire = NativeRef.Thing(f), Home = map.areaManager.Home[f.Position], Size = Number(f.fireSize) }).ToList();
                result.Fires.AddRange(values);
            });
            Read("filth", result, () => {
                // Home-area filth only: a map carries hundreds of natural
                // dirt and rubble rows outside it that no clean order may
                // ever target (upkeep orders require the home area), and a
                // whole-map census exceeded the bound on every real map.
                var rows = sets.Filth.Where(f => map.areaManager.Home[f.Position]).OrderBy(f => f.thingIDNumber).ToList();
                var values = rows.Select(f => {
                    var value = new Obs.FilthState { Filth = NativeRef.Thing(f), Home = map.areaManager.Home[f.Position], Thickness = checked((uint)f.thickness) };
                    var room = f.GetRoom();
                    if (room?.Role != null) value.RoomRole = Id(room.Role.defName);
                    // The same room identity the typed room census reports, so
                    // the controller can pair filth with a measured room
                    // cleanliness instead of a role name alone.
                    if (room != null) value.Room = NativeRef.Room(room);
                    return value;
                }).ToList();
                result.Filth.AddRange(values);
            });
            // Home-area auto-expand, a save-level setting (#1322).
            Read("auto_home_area", result, () => { if (Find.PlaySettings != null) result.AutoHomeArea = Find.PlaySettings.autoHomeArea; });
            // Current home cells (#1328): the planner diffs its target against them.
            Read("home_cells", result, () => result.HomeCells.AddRange(map.areaManager.Home.ActiveCells.Select(Cell)));
            Read("home_coverage", result, () => {
                var state = HomeCoverage.State(map);
                var targets = HomeCoverage.Targets(map).OrderBy(id => id, StringComparer.Ordinal).ToList();
                var facts = new Obs.HomeCoverageFacts { Revision = state.Revision };
                foreach (var target in targets) {
                    var full = HomeCoverage.FullScope(map, target);
                    if (full == null) {
                        facts.Targets.Add(new Obs.HomeCoverageTarget { Id = Id(target), Blocker = "Bounded visible native facility geometry unavailable" });
                        continue;
                    }
                    var cells = HomeCoverageGeometry.Batch(full, c => map.areaManager.Home[c]).OrderBy(c => c.x).ThenBy(c => c.z).ToList();
                    var missing = cells.Where(c => !map.areaManager.Home[c]).ToList();
                    // Retained wire field: autonomous Home has no exclusions.
                    var row = new Obs.HomeCoverageTarget { Id = Id(target), ShapeToken = HomeCoverage.Shape(target, cells),
                        MissingCells = checked((uint)missing.Count), ExcludedCells = 0 };
                    row.Cells.AddRange(cells.Select(Cell));
                    row.ExtentGeometry = new Obs.HomeExtentGeometry();
                    var building = map.listerBuildings.allBuildingsColonist.ById(target);
                    if (building != null) {
                        var footprint = new HashSet<IntVec3>(building.OccupiedRect());
                        foreach (var cell in full.Where(c => !footprint.Contains(c))) {
                            // Internal doors are the observed connections between enclosed rooms.
                            if (cell.GetEdifice(map) is Building_Door) row.ExtentGeometry.Corridor.Add(Cell(cell));
                            else row.ExtentGeometry.EnclosedInterior.Add(Cell(cell));
                        }
                    } else {
                        // A stockpile's whole footprint, unbatched: colony extent
                        // takes every census stockpile (#719).
                        row.ExtentGeometry.Zone.AddRange(full.OrderBy(c => c.x).ThenBy(c => c.z).Select(Cell));
                    }
                    facts.Targets.Add(row);
                }
                result.HomeCoverage = new Obs.HomeCoverageSection { Observed = facts };
            });
            Read("lighting", result, () => {
                // Work cells are the interaction cells of colonist benches (work
                // tables and research benches): the cell a pawn stands on while
                // working, which is what RimWorld's darkness penalties measure.
                var benches = sets.PlayerBuildings.Where(b => b.def.hasInteractionCell
                    && (b is Building_WorkTable || b is Building_ResearchBench)).OrderBy(b => b.thingIDNumber).ToList();
                var lamps = sets.PlayerBuildings.Where(b => b.TryGetComp<CompGlower>() != null)
                    .OrderBy(b => b.thingIDNumber).ToList();
                var facts = new Obs.LightingFacts();
                foreach (var b in benches) {
                    var cell = b.InteractionCell;
                    var row = new Obs.WorkLightCell { Bench = NativeBuildingObservationTools.Ref(b), Cell = Cell(cell), Glow = Number(map.glowGrid.GroundGlowAt(cell)), Roofed = cell.Roofed(map) };
                    var room = cell.GetRoom(map);
                    if (room != null)
                    {
                        row.Room = NativeRef.Room(room);
                        // A room growing a plant that dies to light (cave
                        // fungus) is protected: lighting it kills the crop.
                        row.LightSensitive = room.ProperRoom && room.Cells.Any(c => c.GetPlant(map) is Plant plant && plant.def.plant != null && plant.def.plant.diesToLight
                            || c.GetZone(map) is Zone_Growing zone && zone.GetPlantDefToGrow()?.plant?.diesToLight == true);
                    }
                    facts.WorkCells.Add(row);
                }
                foreach (var b in lamps) {
                    var glower = b.TryGetComp<CompGlower>();
                    var row = new Obs.LampState { Building = NativeBuildingObservationTools.Ref(b),
                        GlowRadius = Number(glower.Props.glowRadius), Lit = glower.Glows };
                    var room = b.Position.GetRoom(map);
                    if (room != null) row.Room = NativeRef.Room(room);
                    facts.Lamps.Add(row);
                }
                result.Lighting = new Obs.LightingSection { Observed = facts };
            });
            Read("flooring", result, () => {
                // Every proper indoor room the colony lives in (any cell in
                // the home area, not psychologically outdoors) with the
                // terrain under each cell; the catalog's terrain rows carry the
                // stats MaintainFlooring scores. A floor blueprint
                // or frame on a cell is reported as its pending terrain so
                // the planner never doubles an open order.
                var rooms = map.regionGrid.AllRooms.Where(r => r.ProperRoom && !r.PsychologicallyOutdoors && !r.TouchesMapEdge
                    && !r.Fogged && r.Cells.Any(c => map.areaManager.Home[c])).OrderBy(r => r.ID).ToList();
                var facts = new Obs.FlooringFacts();
                foreach (var r in rooms) {
                    var row = new Obs.FloorRoom { Room = NativeRef.Room(r) };
                    if (r.Role != null) row.Role = Id(r.Role.defName);
                    foreach (var c in r.Cells.OrderBy(c => c.z).ThenBy(c => c.x)) {
                        var terrain = c.GetTerrain(map);
                        var cell = new Obs.FloorCell { Cell = Cell(c), Terrain = Id(terrain.defName) };
                        var pending = c.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild is TerrainDef);
                        if (pending != null) cell.Pending = Id(pending.def.entityDefToBuild.defName);
                        row.Cells.Add(cell);
                    }
                    facts.Rooms.Add(row);
                }
                result.Flooring = new Obs.FlooringSection { Observed = facts };
            });
            Read("routes", result, () => {
                // Every facility a colonist must reach, with each mobile
                // colonist's native reachability from where they stand and
                // the cost of the path the game itself would walk (bounded:
                // the first 256 reachable pairs are measured, the rest
                // report reachability only). A facility no colonist reaches
                // lists breach candidates: player wall cells on its room's
                // border whose outer neighbour some colonist can stand on.
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.Spawned).OrderBy(p => p.thingIDNumber).ToList();
                var player = Faction.OfPlayerSilentFail;
                var facilities = new System.Collections.Generic.List<(Thing thing, Obs.RouteFacilityKind kind, IntVec3 cell)>();
                foreach (var b in sets.PlayerBuildings.OfType<Building_Bed>().Where(b => b.def.building.bed_humanlike && !b.ForPrisoners).OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, Obs.RouteFacilityKind.Bed, b.Position));
                foreach (var b in sets.PlayerBuildings.OfType<Building_WorkTable>().OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, Obs.RouteFacilityKind.Bench, b.InteractionCell));
                foreach (var b in sets.PlayerBuildings.OfType<Building_Storage>().OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, Obs.RouteFacilityKind.Storage, b.Position));
                foreach (var b in sets.PlayerBuildings.Where(b => b.def.surfaceType == SurfaceType.Eat).OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, Obs.RouteFacilityKind.Dining, b.Position));
                foreach (var b in sets.PlayerBuildings.OfType<Building_Turret>().OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, Obs.RouteFacilityKind.Defense, b.Position));
                var facts = new Obs.RoutesFacts();
                facts.PawnIds.AddRange(people.Select(p => Id(p.GetUniqueLoadID())));
                var measured = 0; long pathTicks = 0, reachTicks = 0;
                Obs.RouteFacility Facility(Common.Ref reference, Obs.RouteFacilityKind kind, IntVec3 cell, Func<Pawn, bool> reaches, Func<Pawn, LocalTargetInfo> target, PathEndMode mode)
                {
                    var row = new Obs.RouteFacility { Facility = reference, Kind = kind, Cell = Cell(cell) };
                    var room = cell.GetRoom(map);
                    if (room != null && room.ProperRoom && !room.PsychologicallyOutdoors) row.Room = NativeRef.Room(room);
                    // A door frame is walkable before the door stands, so a
                    // measured path may cross an ordered door; those cells are
                    // reported as pending breaches of a reachable facility so
                    // the controller holds its latch until the door lands
                    // instead of releasing on a half-built wall.
                    Thing? PendingDoor(IntVec3 c) => c.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild is ThingDef td && td.IsDoor);
                    var crossed = new System.Collections.Generic.SortedDictionary<int, (IntVec3 cell, Thing door)>();
                    var anyReach = false;
                    foreach (var p in people)
                    {
                        var rf0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var travel = new Obs.RouteTravel { PawnId = Id(p.GetUniqueLoadID()), Reachable = reaches(p) };
                        reachTicks += System.Diagnostics.Stopwatch.GetTimestamp() - rf0;
                        if (travel.Reachable)
                        {
                            anyReach = true;
                            if (measured < 256)
                            {
                                measured++;
                                var pf0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                using (var path = map.pathFinder.FindPathNow(p.Position, target(p), TraverseParms.For(p, Danger.Some), peMode: mode))
                                {
                                    if (path.Found)
                                    {
                                        travel.PathCost = checked((int)Math.Min(path.TotalCost, int.MaxValue)); travel.PathCells = path.NodesLeftCount;
                                        foreach (var node in path.NodesReversed)
                                        {
                                            var door = PendingDoor(node);
                                            if (door != null) crossed[map.cellIndices.CellToIndex(node)] = (node, door);
                                        }
                                    }
                                }
                                pathTicks += System.Diagnostics.Stopwatch.GetTimestamp() - pf0;
                            }
                        }
                        row.Travel.Add(travel);
                    }
                    if (anyReach)
                    {
                        foreach (var (bc, door) in crossed.Values.Take(16))
                            row.Breaches.Add(new Obs.RouteBreach { Cell = Cell(bc), Edifice = Id(door.def.defName), Pending = Id(door.def.entityDefToBuild.defName), Distance = 0 });
                    }
                    else if (people.Count > 0 && room != null && room.ProperRoom && !room.TouchesMapEdge)
                    {
                        var breaches = new System.Collections.Generic.List<(IntVec3 cell, string edifice, string? pending, int distance)>();
                        // BorderCells repeats a corner shared by two regions.
                        foreach (var edge in room.BorderCells.Distinct())
                        {
                            var wall = edge.GetEdifice(map);
                            var pendingDoor = PendingDoor(edge);
                            var placeable = wall != null && wall.Faction == player && wall.def.building != null && wall.def.building.isPlaceOverableWall && wall.def.size.x == 1 && wall.def.size.z == 1;
                            if (pendingDoor == null && !placeable) continue;
                            var best = int.MaxValue;
                            // A door passes straight through: the cell behind
                            // it must be the room itself (a corner opens nothing).
                            foreach (var neighbour in GenAdj.CardinalDirections)
                            {
                                var outside = edge + neighbour;
                                var inside = edge - neighbour;
                                if (!outside.InBounds(map) || room.Cells.Contains(outside) || !outside.Standable(map) || !room.Cells.Contains(inside)) continue;
                                foreach (var p in people)
                                {
                                    if (!p.CanReach(outside, PathEndMode.OnCell, Danger.Some)) continue;
                                    best = Math.Min(best, p.Position.DistanceToSquared(outside));
                                }
                            }
                            if (pendingDoor == null && best == int.MaxValue) continue;
                            var edifice = wall != null ? wall.def.defName : pendingDoor!.def.defName;
                            breaches.Add((edge, edifice, pendingDoor?.def.entityDefToBuild.defName, best == int.MaxValue ? 0 : best));
                        }
                        foreach (var (bc, edifice, pending, distance) in breaches.OrderBy(b => b.distance).ThenBy(b => b.cell.z).ThenBy(b => b.cell.x).Take(16))
                        {
                            var breach = new Obs.RouteBreach { Cell = Cell(bc), Edifice = Id(edifice), Distance = distance };
                            if (pending != null) breach.Pending = Id(pending);
                            row.Breaches.Add(breach);
                        }
                    }
                    return row;
                }
                foreach (var (thing, kind, cell) in facilities)
                {
                    var mode = thing.def.hasInteractionCell ? PathEndMode.InteractionCell : PathEndMode.Touch;
                    facts.Facilities.Add(Facility(NativeBuildingObservationTools.Ref(thing), kind, cell, p => !thing.IsForbidden(p) && p.CanReach(thing, mode, Danger.Some), p => thing, mode));
                }
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z => z.ID).ToList();
                foreach (var zone in stockpiles)
                {
                    var cell = zone.Cells.OrderBy(c => c.z).ThenBy(c => c.x).FirstOrDefault(c => c.Standable(map));
                    if (cell == default) continue;
                    var reference = new Common.Ref { Id = Id("zone-" + zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
                    facts.Facilities.Add(Facility(reference, Obs.RouteFacilityKind.Stockpile, cell, p => p.CanReach(cell, PathEndMode.OnCell, Danger.Some), p => cell, PathEndMode.OnCell));
                }
                var traffic = map.GetComponent<TrafficState>();
                if (traffic != null)
                {
                    // The busiest TrafficTop cells of each layer (#817);
                    // traffic_samples is the colonist layer's decayed total.
                    facts.TrafficSamples = traffic.Counts.Total(TrafficCounts.Colonist);
                    if (traffic.SinceTick >= 0) facts.TrafficSinceTick = traffic.SinceTick;
                    for (int layer = 0; layer < TrafficCounts.Layers; layer++)
                        foreach (var index in traffic.Counts.Top(layer, TrafficTop))
                        {
                            var at = map.cellIndices.IndexToCell(index);
                            var cell = new Obs.TrafficCell { Cell = Cell(at), Samples = traffic.Counts[layer, index], Terrain = Id(at.GetTerrain(map).defName), Home = map.areaManager.Home[at], Layer = (Obs.TrafficLayer)(layer + 1) };
                            var pending = at.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild is TerrainDef);
                            if (pending != null) cell.Pending = Id(pending.def.entityDefToBuild.defName);
                            facts.Traffic.Add(cell);
                        }
                }
                ObservationWork.Detail("cf.upkeep.routes.path", pathTicks, measured);
                ObservationWork.Detail("cf.upkeep.routes.reach", reachTicks);
                result.Routes = new Obs.RoutesSection { Observed = facts };
            });
            Read("people", result, () => {
                var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                Obs.UpkeepPerson Person(Pawn p) => new Obs.UpkeepPerson {
                    Pawn = NativePawnObservationTools.Ref(p), OwnedBed = NativeRef.Of(p.ownership?.OwnedBed),
                    ComfortableMinC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMax)), TemperatureC = Number(p.AmbientTemperature)
                };
                var values = people.Select(Person).ToList();
                for (var i = 0; i < people.Count; i++) SleepingRelations(people[i], values[i]);
                result.People.AddRange(values);
                // Slaves of the colony (#1036): MaintainHousing gives each a
                // bed set for slaves.
                result.Slaves.AddRange(map.mapPawns.AllPawnsSpawned.Where(p => p.IsSlaveOfColony && !p.Dead).OrderBy(p => p.thingIDNumber).Select(Person));
            });
            Read("beds", result, () => {
                var beds = sets.PlayerBuildings.OfType<Building_Bed>().OrderBy(b => b.thingIDNumber).ToList();
                var people = map.mapPawns.AllPawnsSpawned.Where(p => (p.IsFreeColonist || p.IsSlaveOfColony) && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                var values = beds.Select(b => {
                    var row = new Obs.UpkeepBed { Bed = NativeBuildingObservationTools.Ref(b), Slots = checked((uint)b.SleepingSlotsCount),
                        Humanlike = b.def.building.bed_humanlike, RestEffectiveness = Number(b.GetStatValue(StatDefOf.BedRestEffectiveness)),
                        Medical = b.Medical, Prisoners = b.ForPrisoners, ForSlaves = b.ForSlaves, Roofed = b.OccupiedRect().All(c => c.Roofed(map)),
                        TemperatureC = Number(b.AmbientTemperature) };
                    var room = b.GetRoom();
                    if (room != null) row.Room = NativeRef.Room(room);
                    if (b.TryGetQuality(out var quality)) row.Quality = quality.ToString(); if (b.Stuff != null) row.Stuff = b.Stuff.defName;
                    var owners = b.OwnersForReading.Select(p => Id(p.GetUniqueLoadID())).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    row.Owners.AddRange(NativeRef.All(owners));
                    row.Users.AddRange(NativeRef.All(people.Where(p => p.CurrentBed() == b).Select(p => Id(p.GetUniqueLoadID()))));
                    row.AccessibleTo.AddRange(NativeRef.All(people.Where(p => !b.IsForbidden(p) && p.CanReach(b, PathEndMode.OnCell, Danger.None)).Select(p => Id(p.GetUniqueLoadID()))));
                    return row;
                }).ToList();
                result.Beds.AddRange(values);
            });
            Read("animals", result, () => {
                var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                    && p.Faction == Faction.OfPlayerSilentFail).OrderBy(p => p.thingIDNumber).ToList();
                var food = sets.Items.Where(t => (t.Faction == null || t.Faction == Faction.OfPlayerSilentFail)
                    && t.def.IsNutritionGivingIngestible && !t.def.IsDrug && t.IngestibleNow).ToList();
                var benches = sets.PlayerBuildings.OfType<Building_WorkTable>()
                    .OrderBy(b => b.thingIDNumber).ToList();
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z => z.ID).ToList();
                var feedDefs = FeedDefs.Value;
                var haulers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();
                var values = animals.Select(p => {
                    var requiresPen = AnimalPenUtility.NeedsToBeManagedByRope(p);
                    var suitable = requiresPen ? AnimalPenUtility.ClosestSuitablePen(p, false) : null;
                    // The herd facts (pen, training, area, care) ride the
                    // pawn table row's animal state (#1343).
                    var value = new Obs.AnimalFeed {
                        Pawn = NativePawnObservationTools.Ref(p),
                        Diet = Id(p.RaceProps.foodType.ToString()), RequiresPen = requiresPen
                    };
                    if (suitable != null) value.SuitablePen = NativeRef.Of(Id(suitable.parent.GetUniqueLoadID()));
                    var reachable = food.Where(t => p.WillEat(t) && !t.IsForbidden(p)
                        && p.CanReach(t, PathEndMode.Touch, Danger.None)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                            || p.playerSettings.AreaRestrictionInPawnCurrentMap[t.Position])).OrderBy(t => t.thingIDNumber).ToList();
                    // Each feed stock references its things table row (#1343).
                    foreach (var item in reachable) {
                        var stock = new Obs.FoodStock { Item = NativeRef.Thing(item),
                            Nutrition = Number(FoodUtility.NutritionForEater(p, item) * item.stackCount) };
                        stock.Eaters.Add(NativeRef.Of(Id(p.GetUniqueLoadID()))!);
                        value.ReachableStoredFeed.Add(stock);
                    }
                    // A bill drops its product at the bench, so only a bench
                    // the animal can walk to inside its allowed area feeds it.
                    var reachableBenches = benches.Where(b => p.CanReach(b, PathEndMode.Touch, Danger.None)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                            || p.playerSettings.AreaRestrictionInPawnCurrentMap[b.Position])).ToList();
                    value.ReachableBenches.AddRange(NativeRef.All(reachableBenches.Select(b => Id(b.GetUniqueLoadID()))));
                    // Feed made elsewhere still feeds the animal once hauled
                    // into a stockpile it can reach: name those zones with
                    // the edible definitions each accepts, and a free
                    // connected footprint in the area where one could go.
                    var area = p.playerSettings?.AreaRestrictionInPawnCurrentMap;
                    bool Allowed(IntVec3 c) => area == null || area[c];
                    bool AnimalReach(IntVec3 c) => Allowed(c) && p.CanReach(c, PathEndMode.OnCell, Danger.None);
                    foreach (var zone in stockpiles) {
                        if (!zone.Cells.Any(AnimalReach)) continue;
                        var storage = new Obs.AnimalFeedStorage { Zone = NativeRef.Of(Id(zone.GetUniqueLoadID())) };
                        storage.Accepts.AddRange(feedDefs.Where(d => p.RaceProps.CanEverEat(d) && zone.settings.filter.Allows(d)).Select(d => Id(d.defName)));
                        value.ReachableStorage.Add(storage);
                    }
                    value.StorageCandidates.AddRange(FeedStorageCandidates(map, p, haulers, Allowed).Select(Cell));
                    return value;
                }).ToList();
                result.Animals.AddRange(values);
            });
            // The factionless animals a MaintainHerd tame write can target:
            // native tame eligibility only, no feed or pen facts. A wild
            // census beyond the bound leaves the section unknown rather than
            // silently truncating the tame candidate list.
            Read("wild_animals", result, () => {
                var wild = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal && p.Faction == null)
                    .OrderBy(p => p.thingIDNumber).ToList();
                result.WildAnimals.AddRange(wild.Select(p => new Obs.AnimalFeed {
                    Pawn = NativePawnObservationTools.Ref(p), Diet = Id(p.RaceProps.foodType.ToString()), RequiresPen = false }));
            });
        }

        // TrafficTop bounds the cells reported per traffic layer (#817).
        private const int TrafficTop = 128;

        // FeedStorageCandidates floods outward from the animal over the free
        // cells of its allowed area (roofed and not marked to collapse, as the
        // stockpile zone operation requires, standable, unzoned, no building,
        // blueprint, frame or item, reachable by the animal and by an eligible
        // hauler) and returns the first connected footprint of up to
        // feedStorageCandidateCells cells, or nothing when no hauler exists or
        // no cell qualifies. The flood is bounded so a wide area costs a
        // bounded number of reachability checks.
        private const int feedStorageCandidateCells = 8;
        private const int feedStorageFloodBound = 256;
        // The food item defs, sorted by name: fixed for the process, so scanned once
        // rather than per frame.
        private static readonly Lazy<List<ThingDef>> FeedDefs = new Lazy<List<ThingDef>>(() =>
            DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.category == ThingCategory.Item && NativeFoodPolicy.IsFood(d))
                .OrderBy(d => d.defName, StringComparer.Ordinal).ToList());

        private static List<IntVec3> FeedStorageCandidates(Map map, Pawn animal, List<Pawn> haulers, Func<IntVec3, bool> allowed)
        {
            var result = new List<IntVec3>();
            if (haulers.Count == 0) return result;
            bool Free(IntVec3 c) => c.InBounds(map) && allowed(c) && !c.Fogged(map) && c.Standable(map)
                && c.Roofed(map) && !map.roofCollapseBuffer.IsMarkedToCollapse(c)
                && map.zoneManager.ZoneAt(c) == null && c.GetEdifice(map) == null
                && !c.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t.def.category == ThingCategory.Item);
            bool Reachable(IntVec3 c) => animal.CanReach(c, PathEndMode.OnCell, Danger.None)
                && haulers.Any(h => !c.IsForbidden(h) && h.CanReach(c, PathEndMode.OnCell, Danger.None));
            var seed = GenRadial.RadialCellsAround(animal.Position, 12, true).Where(c => Free(c) && Reachable(c)).Cast<IntVec3?>().FirstOrDefault();
            if (seed == null) return result;
            var seen = new HashSet<IntVec3> { seed.Value };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(seed.Value);
            while (queue.Count > 0 && result.Count < feedStorageCandidateCells && seen.Count < feedStorageFloodBound) {
                var cell = queue.Dequeue();
                result.Add(cell);
                foreach (var next in GenAdj.CardinalDirections.Select(d => cell + d)) {
                    if (!seen.Add(next) || !Free(next) || !Reachable(next)) continue;
                    queue.Enqueue(next);
                }
            }
            return result;
        }

        // SleepingRelations fills a colonist's partners (lover, spouse and
        // fiance relations to living pawns on the same map), the native
        // willingness to share a bed with each of them (the plain SharedBed
        // precept when there is none) and the most senior royal title.
        private static void SleepingRelations(Pawn p, Obs.UpkeepPerson row)
        {
            var partners = (p.relations?.DirectRelations ?? new List<DirectPawnRelation>())
                .Where(r => LovePartnerRelationUtility.IsLovePartnerRelation(r.def)
                    && r.otherPawn != null && !r.otherPawn.Dead && r.otherPawn.Spawned && r.otherPawn.Map == p.Map)
                .Select(r => r.otherPawn).Distinct().OrderBy(o => o.thingIDNumber).ToList();
            row.Partners.AddRange(NativeRef.All(partners.Select(o => Id(o.GetUniqueLoadID()))));
            row.BedSharingAllowed = partners.Count == 0 ? IdeoUtility.DoerWillingToDo(HistoryEventDefOf.SharedBed, p) : partners.All(o => BedUtility.WillingToShareBed(p, o));
            if (!ModsConfig.RoyaltyActive) return;
            var title = p.royalty?.MostSeniorTitle?.def;
            if (title == null) return;
            var facts = new Obs.RoyalTitleFacts { DefName = Id(title.defName), Seniority = title.seniority };
            foreach (var req in title.GetBedroomRequirements(p) ?? Enumerable.Empty<RoomRequirement>()) {
                if (req.disablingPrecepts != null && p.Ideo != null && p.Ideo.PreceptsListForReading.Any(x => req.disablingPrecepts.Contains(x.def))) continue;
                switch (req) {
                    case RoomRequirement_Area area: facts.BedroomMinArea = Math.Max(facts.BedroomMinArea, area.area); break;
                    case RoomRequirement_Impressiveness imp: facts.BedroomMinImpressiveness = Math.Max(facts.BedroomMinImpressiveness, imp.impressiveness); break;
                    case RoomRequirement_TerrainWithTags _: facts.BedroomFloored = true; break;
                    case RoomRequirement_AllThingsAnyOfAreGlowing _: case RoomRequirement_HasAssignedThroneAnyOf _: break;
                    case RoomRequirement_ThingAnyOfCount anyCount: facts.BedroomThings.Add(Things(anyCount.things, anyCount.count)); break;
                    case RoomRequirement_ThingAnyOf any: facts.BedroomThings.Add(Things(any.things, 1)); break;
                    case RoomRequirement_ThingCount count: facts.BedroomThings.Add(Things(new List<ThingDef> { count.thingDef }, count.count)); break;
                    case RoomRequirement_Thing thing: facts.BedroomThings.Add(Things(new List<ThingDef> { thing.thingDef }, 1)); break;
                }
            }
            row.Title = facts;
        }

        private static Obs.BedroomThingRequirement Things(List<ThingDef> defs, int count)
        {
            var row = new Obs.BedroomThingRequirement { Count = count };
            row.AnyOf.AddRange(defs.Where(d => d != null).Select(d => Id(d.defName)));
            return row;
        }

        private static void Read(string field, Obs.UpkeepFacts result, Action read)
        {
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            try { read(); }
            catch (Exception) { result.Issues.Add(Issue(field, Common.UnavailableReason.ReadFailed, "Complete native upkeep section is unavailable.")); }
            ObservationWork.Detail("cf.upkeep." + field, System.Diagnostics.Stopwatch.GetTimestamp() - began);
        }
    }
}
