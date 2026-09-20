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
        internal static void Populate(Map map, Obs.UpkeepFacts result)
        {
            var things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
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
                var rows = things.Where(t => t.def.category == ThingCategory.Item && !t.def.IsCorpse
                    && (t.Faction == null || t.Faction == Faction.OfPlayerSilentFail)
                    && (map.areaManager.Home[t.Position] || t.IsInValidStorage() || t.def.IsMedicine
                        || t.def.GetStatValueAbstract(StatDefOf.DeteriorationRate, t.Stuff) > 0f
                        || (t.TryGetComp<CompRottable>()?.Active ?? false))).OrderBy(t => t.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(t => {
                    var rot = t.TryGetComp<CompRottable>();
                    var value = new Obs.UpkeepItem {
                        Item = Ref(t), Count = t.stackCount, Roofed = t.Position.Roofed(map), InStorage = t.IsInValidStorage(),
                        DeteriorationRate = Number(t.GetStatValue(StatDefOf.DeteriorationRate)),
                        BaseDeteriorationRate = Number(t.def.GetStatValueAbstract(StatDefOf.DeteriorationRate, t.Stuff)),
                        Forbidden = t.IsForbidden(Faction.OfPlayerSilentFail), Medicine = t.def.IsMedicine,
                        Perishable = rot != null && rot.Active
                    };
                    if (rot != null && rot.Active) value.RotTicks = Math.Max(0, rot.TicksUntilRotAtCurrentTemp);
                    return value;
                }).ToList();
                result.Items.AddRange(values);
            });
            Read("structures", result, () => {
                // Non-damageable markers (including sleeping spots) have a
                // native -1 sentinel and cannot be repair targets.
                var rows = things.OfType<Building>().Where(b => b.Faction == Faction.OfPlayerSilentFail && b.def.useHitPoints).OrderBy(b => b.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(b => new Obs.UpkeepStructure {
                    Building = new Obs.BuildingState { Building = Ref(b), HitPoints = b.HitPoints, MaxHitPoints = b.MaxHitPoints },
                    Home = b.OccupiedRect().All(c => map.areaManager.Home[c]), Flammability = Number(b.GetStatValue(StatDefOf.Flammability)),
                    RepairPriority = b.TryGetComp<CompTempControl>() != null || b.TryGetComp<CompPowerPlant>() != null
                        || b is Building_Bed bed && bed.Medical ? 0
                        : b.def.holdsRoof || b is Building_WorkTable || b is Building_Bed ? 1 : 2
                }).ToList();
                result.Structures.AddRange(values);
            });
            Read("fires", result, () => {
                var rows = things.OfType<Fire>().OrderBy(f => f.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(f => new Obs.FireState { Fire = Ref(f), Home = map.areaManager.Home[f.Position], Size = Number(f.fireSize) }).ToList();
                result.Fires.AddRange(values);
            });
            Read("filth", result, () => {
                // Home-area filth only: a map carries hundreds of natural
                // dirt and rubble rows outside it that no clean order may
                // ever target (upkeep orders require the home area), and a
                // whole-map census exceeded the bound on every real map.
                var rows = things.OfType<Filth>().Where(f => map.areaManager.Home[f.Position]).OrderBy(f => f.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(f => {
                    var value = new Obs.FilthState { Filth = Ref(f), Home = map.areaManager.Home[f.Position], Thickness = checked((uint)f.thickness) };
                    var room = f.GetRoom();
                    if (room?.Role != null) value.RoomRole = Id(room.Role.defName);
                    // The same room identity the typed room census reports, so
                    // the controller can pair filth with a measured room
                    // cleanliness instead of a role name alone.
                    if (room != null) value.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return value;
                }).ToList();
                result.Filth.AddRange(values);
            });
            Read("home_coverage", result, () => {
                var state = HomeCoverage.State(map);
                var targets = HomeCoverage.Targets(map).OrderBy(id => id, StringComparer.Ordinal).ToList();
                Require(targets.Count, 256);
                var facts = new Obs.HomeCoverageFacts { Revision = state.Revision };
                foreach (var target in targets) {
                    var full = HomeCoverage.FullScope(map, target);
                    if (full == null) {
                        facts.Targets.Add(new Obs.HomeCoverageTarget { Id = Id(target), Blocker = "Bounded visible native facility geometry unavailable" });
                        continue;
                    }
                    Require(full.Count, 65536);
                    var cells = HomeCoverageGeometry.Batch(full, c => map.areaManager.Home[c]).OrderBy(c => c.x).ThenBy(c => c.z).ToList();
                    var missing = cells.Where(c => !map.areaManager.Home[c]).ToList();
                    // Retained wire field: autonomous Home has no exclusions.
                    var row = new Obs.HomeCoverageTarget { Id = Id(target), ShapeToken = HomeCoverage.Shape(target, cells),
                        MissingCells = checked((uint)missing.Count), ExcludedCells = 0 };
                    row.Cells.AddRange(cells.Select(Cell));
                    row.ExtentGeometry = new Obs.HomeExtentGeometry();
                    var building = map.listerBuildings.allBuildingsColonist.SingleOrDefault(b => b.GetUniqueLoadID() == target);
                    if (building != null) {
                        var footprint = new HashSet<IntVec3>(building.OccupiedRect());
                        foreach (var cell in full.Where(c => !footprint.Contains(c))) {
                            // Internal doors are the observed connections between enclosed rooms.
                            if (cell.GetEdifice(map) is Building_Door) row.ExtentGeometry.Corridor.Add(Cell(cell));
                            else row.ExtentGeometry.EnclosedInterior.Add(Cell(cell));
                        }
                    }
                    facts.Targets.Add(row);
                }
                facts.Completeness = Complete(facts.Targets.Count);
                result.HomeCoverage = new Obs.HomeCoverageSection { Observed = facts };
            });
            Read("lighting", result, () => {
                // Work cells are the interaction cells of colonist benches (work
                // tables and research benches): the cell a pawn stands on while
                // working, which is what RimWorld's darkness penalties measure.
                var benches = things.OfType<Building>().Where(b => b.Faction == Faction.OfPlayerSilentFail && b.def.hasInteractionCell
                    && (b is Building_WorkTable || b is Building_ResearchBench)).OrderBy(b => b.thingIDNumber).ToList();
                var lamps = things.OfType<Building>().Where(b => b.Faction == Faction.OfPlayerSilentFail && b.TryGetComp<CompGlower>() != null)
                    .OrderBy(b => b.thingIDNumber).ToList();
                Require(benches.Count, 256); Require(lamps.Count, 256);
                var facts = new Obs.LightingFacts();
                foreach (var b in benches) {
                    var cell = b.InteractionCell;
                    var row = new Obs.WorkLightCell { Bench = Ref(b), Cell = Cell(cell), Glow = Number(map.glowGrid.GroundGlowAt(cell)), Roofed = cell.Roofed(map) };
                    var room = cell.GetRoom(map);
                    if (room != null)
                    {
                        row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        // A room growing a plant that dies to light (cave
                        // fungus) is protected: lighting it kills the crop.
                        row.LightSensitive = room.ProperRoom && room.Cells.Any(c => c.GetPlant(map) is Plant plant && plant.def.plant != null && plant.def.plant.diesToLight
                            || c.GetZone(map) is Zone_Growing zone && zone.GetPlantDefToGrow()?.plant?.diesToLight == true);
                    }
                    facts.WorkCells.Add(row);
                }
                foreach (var b in lamps) {
                    var glower = b.TryGetComp<CompGlower>();
                    var service = new Obs.BuildingServiceState { SwitchedOn = b.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true };
                    var power = b.TryGetComp<CompPowerTrader>();
                    if (power != null) { service.Connected = power.PowerNet != null; service.PowerOn = power.PowerOn; service.PowerOutputW = Number(power.PowerOutput); }
                    service.BrokenDown = b.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false;
                    var fuel = b.TryGetComp<CompRefuelable>();
                    if (fuel != null) {
                        service.Fuel = Number(fuel.Fuel); service.TargetFuel = Number(fuel.TargetFuelLevel); service.OutOfFuel = !fuel.HasFuel;
                        var defs = fuel.Props.fuelFilter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToList();
                        Require(defs.Count, 256); service.AllowedFuelDefs.Add(defs);
                    }
                    var row = new Obs.LampState { Building = new Obs.BuildingState { Building = Ref(b), Service = service },
                        GlowRadius = Number(glower.Props.glowRadius), Lit = glower.Glows };
                    var room = b.Position.GetRoom(map);
                    if (room != null) row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    facts.Lamps.Add(row);
                }
                facts.Completeness = Complete(benches.Count + lamps.Count);
                result.Lighting = new Obs.LightingSection { Observed = facts };
            });
            Read("flooring", result, () => {
                // Every proper indoor room the colony lives in (any cell in
                // the home area, not psychologically outdoors) with the
                // terrain under each cell; the terrain table carries the
                // abstract stats MaintainFlooring scores. A floor blueprint
                // or frame on a cell is reported as its pending terrain so
                // the planner never doubles an open order.
                var rooms = map.regionGrid.AllRooms.Where(r => r.ProperRoom && !r.PsychologicallyOutdoors && !r.TouchesMapEdge
                    && !r.Fogged && r.Cells.Any(c => map.areaManager.Home[c])).OrderBy(r => r.ID).ToList();
                Require(rooms.Count, 256);
                var facts = new Obs.FlooringFacts();
                var terrains = new System.Collections.Generic.SortedDictionary<string, TerrainDef>(StringComparer.Ordinal);
                var cells = 0;
                foreach (var r in rooms) {
                    var row = new Obs.FloorRoom { RoomId = r.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    if (r.Role != null) row.Role = Id(r.Role.defName);
                    foreach (var c in r.Cells.OrderBy(c => c.z).ThenBy(c => c.x)) {
                        var terrain = c.GetTerrain(map);
                        var cell = new Obs.FloorCell { Cell = Cell(c), Terrain = Id(terrain.defName) };
                        terrains[terrain.defName] = terrain;
                        var pending = c.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild is TerrainDef);
                        if (pending != null) cell.Pending = Id(pending.def.entityDefToBuild.defName);
                        row.Cells.Add(cell);
                    }
                    cells += row.Cells.Count;
                    Require(cells, 4096);
                    facts.Rooms.Add(row);
                }
                // The traffic tier scores the routes census's most-travelled
                // cells against the same table, so their terrains are named too.
                var traffic = map.GetComponent<TrafficState>();
                if (traffic != null)
                    foreach (var pair in traffic.Samples.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.z).ThenBy(kv => kv.Key.x).Take(64)) {
                        var terrain = pair.Key.GetTerrain(map);
                        terrains[terrain.defName] = terrain;
                    }
                foreach (var terrain in terrains.Values)
                    facts.Terrains.Add(new Obs.FloorTerrain { DefName = Id(terrain.defName), Natural = terrain.natural, PathCost = terrain.pathCost,
                        Cleanliness = Number(terrain.GetStatValueAbstract(StatDefOf.Cleanliness)), Beauty = Number(terrain.GetStatValueAbstract(StatDefOf.Beauty)),
                        Flammability = Number(terrain.GetStatValueAbstract(StatDefOf.Flammability)) });
                facts.Completeness = Complete(rooms.Count);
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
                Require(people.Count, 32);
                var player = Faction.OfPlayerSilentFail;
                var facilities = new System.Collections.Generic.List<(Thing thing, string kind, IntVec3 cell)>();
                foreach (var b in things.OfType<Building_Bed>().Where(b => b.Faction == player && b.def.building.bed_humanlike && !b.ForPrisoners).OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, "bed", b.Position));
                foreach (var b in things.OfType<Building_WorkTable>().Where(b => b.Faction == player).OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, "bench", b.InteractionCell));
                foreach (var b in things.OfType<Building_Storage>().Where(b => b.Faction == player).OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, "storage", b.Position));
                foreach (var b in things.OfType<Building>().Where(b => b.Faction == player && b.def.surfaceType == SurfaceType.Eat).OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, "dining", b.Position));
                foreach (var b in things.OfType<Building_Turret>().Where(b => b.Faction == player).OrderBy(b => b.thingIDNumber))
                    facilities.Add((b, "defense", b.Position));
                Require(facilities.Count, 128);
                var facts = new Obs.RoutesFacts();
                facts.PawnIds.AddRange(people.Select(p => Id(p.GetUniqueLoadID())));
                var measured = 0;
                Obs.RouteFacility Facility(Obs.EntityRef reference, string kind, IntVec3 cell, Func<Pawn, bool> reaches, Func<Pawn, LocalTargetInfo> target, PathEndMode mode)
                {
                    var row = new Obs.RouteFacility { Facility = reference, Kind = kind, Cell = Cell(cell) };
                    var room = cell.GetRoom(map);
                    if (room != null && room.ProperRoom && !room.PsychologicallyOutdoors) row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
                        var travel = new Obs.RouteTravel { PawnId = Id(p.GetUniqueLoadID()), Reachable = reaches(p) };
                        if (travel.Reachable)
                        {
                            anyReach = true;
                            if (measured < 256)
                            {
                                measured++;
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
                    facts.Facilities.Add(Facility(Ref(thing), kind, cell, p => !thing.IsForbidden(p) && p.CanReach(thing, mode, Danger.Some), p => thing, mode));
                }
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z => z.ID).ToList();
                Require(facilities.Count + stockpiles.Count, 128);
                foreach (var zone in stockpiles)
                {
                    var cell = zone.Cells.OrderBy(c => c.z).ThenBy(c => c.x).FirstOrDefault(c => c.Standable(map));
                    if (cell == default) continue;
                    var reference = new Obs.EntityRef { Id = Id("zone-" + zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture)), DefName = Id("Zone_Stockpile"), MapId = map.uniqueID, Position = Cell(cell) };
                    facts.Facilities.Add(Facility(reference, "stockpile", cell, p => p.CanReach(cell, PathEndMode.OnCell, Danger.Some), p => cell, PathEndMode.OnCell));
                }
                var traffic = map.GetComponent<TrafficState>();
                if (traffic != null)
                {
                    facts.TrafficSamples = traffic.Total;
                    if (traffic.SinceTick >= 0) facts.TrafficSinceTick = traffic.SinceTick;
                    foreach (var pair in traffic.Samples.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.z).ThenBy(kv => kv.Key.x).Take(64))
                    {
                        var cell = new Obs.TrafficCell { Cell = Cell(pair.Key), Samples = pair.Value, Terrain = Id(pair.Key.GetTerrain(map).defName), Home = map.areaManager.Home[pair.Key] };
                        var pending = pair.Key.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild is TerrainDef);
                        if (pending != null) cell.Pending = Id(pending.def.entityDefToBuild.defName);
                        facts.Traffic.Add(cell);
                    }
                }
                facts.Completeness = Complete(facts.Facilities.Count);
                result.Routes = new Obs.RoutesSection { Observed = facts };
            });
            Read("people", result, () => {
                var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                Require(people.Count, 256);
                var values = people.Select(p => new Obs.UpkeepPerson {
                    Pawn = new Obs.PawnState { Pawn = Ref(p) }, OwnedBedId = p.ownership?.OwnedBed?.GetUniqueLoadID() ?? "",
                    ComfortableMinC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMax)), TemperatureC = Number(p.AmbientTemperature)
                }).ToList();
                result.People.AddRange(values);
            });
            Read("beds", result, () => {
                var beds = things.OfType<Building_Bed>().Where(b => b.Faction == Faction.OfPlayerSilentFail).OrderBy(b => b.thingIDNumber).ToList();
                var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                Require(beds.Count, 256); Require(people.Count, 256);
                var values = beds.Select(b => {
                    var row = new Obs.UpkeepBed { Bed = Ref(b), Slots = checked((uint)b.SleepingSlotsCount),
                        Humanlike = b.def.building.bed_humanlike, RestEffectiveness = Number(b.GetStatValue(StatDefOf.BedRestEffectiveness)),
                        Medical = b.Medical, Prisoners = b.ForPrisoners, Roofed = b.OccupiedRect().All(c => c.Roofed(map)),
                        TemperatureC = Number(b.AmbientTemperature) };
                    var owners = b.OwnersForReading.Select(p => Id(p.GetUniqueLoadID())).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    Require(owners.Count, 256); row.Owners.AddRange(owners);
                    row.Users.AddRange(people.Where(p => p.CurrentBed() == b).Select(p => Id(p.GetUniqueLoadID())));
                    row.AccessibleTo.AddRange(people.Where(p => !b.IsForbidden(p) && p.CanReach(b, PathEndMode.OnCell, Danger.None)).Select(p => Id(p.GetUniqueLoadID())));
                    return row;
                }).ToList();
                result.Beds.AddRange(values);
            });
            Read("animals", result, () => {
                var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                    && p.Faction == Faction.OfPlayerSilentFail).OrderBy(p => p.thingIDNumber).ToList();
                Require(animals.Count, 256);
                var food = things.Where(t => t.def.category == ThingCategory.Item
                    && (t.Faction == null || t.Faction == Faction.OfPlayerSilentFail)
                    && t.def.IsNutritionGivingIngestible && !t.def.IsDrug && t.IngestibleNow).ToList();
                var benches = things.OfType<Building_WorkTable>().Where(b => b.Faction == Faction.OfPlayerSilentFail)
                    .OrderBy(b => b.thingIDNumber).ToList();
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z => z.ID).ToList();
                var feedDefs = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(d => d.category == ThingCategory.Item && d.IsNutritionGivingIngestible && !d.IsDrug && !d.IsCorpse)
                    .OrderBy(d => d.defName, StringComparer.Ordinal).ToList();
                var haulers = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();
                var values = animals.Select(p => {
                    var requiresPen = AnimalPenUtility.NeedsToBeManagedByRope(p);
                    var pen = requiresPen ? AnimalPenUtility.GetCurrentPenOf(p, false) : null;
                    var suitable = requiresPen ? AnimalPenUtility.ClosestSuitablePen(p, false) : null;
                    var state = new Obs.AnimalState {
                        Release = map.designationManager.DesignationOn(p, DesignationDefOf.ReleaseAnimalToWild) != null,
                        Slaughter = map.designationManager.DesignationOn(p, DesignationDefOf.Slaughter) != null,
                        SafeToRelease = NativeHusbandryOperations.Eligible(p) && NativeHusbandryOperations.SafeToRelease(p)
                    };
                    if (requiresPen) state.Contained = pen != null;
                    if (pen != null) state.PenId = Id(pen.parent.GetUniqueLoadID());
                    var value = new Obs.AnimalFeed {
                        Pawn = new Obs.PawnState { Pawn = Ref(p), AnimalState = state },
                        Diet = Id(p.RaceProps.foodType.ToString()), RequiresPen = requiresPen
                    };
                    if (suitable != null) value.SuitablePenId = Id(suitable.parent.GetUniqueLoadID());
                    var reachable = food.Where(t => p.WillEat(t) && !t.IsForbidden(p)
                        && p.CanReach(t, PathEndMode.Touch, Danger.None)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                            || p.playerSettings.AreaRestrictionInPawnCurrentMap[t.Position])).OrderBy(t => t.thingIDNumber).ToList();
                    Require(reachable.Count, 256);
                    foreach (var item in reachable) {
                        var rot = item.TryGetComp<CompRottable>();
                        var stock = new Obs.FoodStock { Item = Ref(item), Count = item.stackCount, HolderId = "",
                            Nutrition = Number(FoodUtility.NutritionForEater(p, item) * item.stackCount),
                            Perishable = rot != null && rot.Active, Roofed = item.Position.Roofed(map) };
                        stock.EaterIds.Add(Id(p.GetUniqueLoadID()));
                        if (rot != null && rot.Active) stock.RotTicks = Math.Max(0, rot.TicksUntilRotAtCurrentTemp);
                        value.ReachableStoredFeed.Add(stock);
                    }
                    // A bill drops its product at the bench, so only a bench
                    // the animal can walk to inside its allowed area feeds it.
                    var reachableBenches = benches.Where(b => p.CanReach(b, PathEndMode.Touch, Danger.None)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                            || p.playerSettings.AreaRestrictionInPawnCurrentMap[b.Position])).ToList();
                    Require(reachableBenches.Count, 256);
                    value.ReachableBenchIds.AddRange(reachableBenches.Select(b => Id(b.GetUniqueLoadID())));
                    // Feed made elsewhere still feeds the animal once hauled
                    // into a stockpile it can reach: name those zones with
                    // the edible definitions each accepts, and a free
                    // connected footprint in the area where one could go.
                    var area = p.playerSettings?.AreaRestrictionInPawnCurrentMap;
                    bool Allowed(IntVec3 c) => area == null || area[c];
                    bool AnimalReach(IntVec3 c) => Allowed(c) && p.CanReach(c, PathEndMode.OnCell, Danger.None);
                    foreach (var zone in stockpiles) {
                        if (!zone.Cells.Any(AnimalReach)) continue;
                        var storage = new Obs.AnimalFeedStorage { ZoneId = Id(zone.GetUniqueLoadID()) };
                        storage.Accepts.AddRange(feedDefs.Where(d => p.RaceProps.CanEverEat(d) && zone.settings.filter.Allows(d)).Select(d => Id(d.defName)));
                        Require(storage.Accepts.Count, 256);
                        value.ReachableStorage.Add(storage);
                    }
                    Require(value.ReachableStorage.Count, 256);
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
                Require(wild.Count, 256);
                result.WildAnimals.AddRange(wild.Select(p => new Obs.AnimalFeed {
                    Pawn = new Obs.PawnState { Pawn = Ref(p), Wild = true, AnimalState = new Obs.AnimalState {
                        Tameable = NativeHusbandryOperations.Tameable(p),
                        Tame = map.designationManager.DesignationOn(p, DesignationDefOf.Tame) != null,
                        MinimumHandlingSkill = TrainableUtility.MinimumHandlingSkill(p) } },
                    Diet = Id(p.RaceProps.foodType.ToString()), RequiresPen = false
                }));
            });
        }

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

        private static Obs.EntityRef Ref(Thing thing) => new Obs.EntityRef {
            Id = Id(thing.GetUniqueLoadID()), DefName = Id(thing.def.defName), MapId = thing.Map.uniqueID, Position = Cell(thing.Position)
        };

        private static void Read(string field, Obs.UpkeepFacts result, Action read)
        {
            try { read(); }
            catch (ReadLimit) { result.Issues.Add(Issue(field, Common.UnavailableReason.LimitExceeded, "Complete upkeep census exceeds 256 rows.")); }
            catch (Exception) { result.Issues.Add(Issue(field, Common.UnavailableReason.ReadFailed, "Complete native upkeep section is unavailable.")); }
        }
    }
}
