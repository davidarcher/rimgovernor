#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Defs = RimGovernor.Contracts.Wire.Defs;
using Obs = RimGovernor.Contracts.Wire.Observations;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // Complete independent censuses: a failed section contributes an issue and
    // no rows. The enclosing colony boundary still enforces its one-MiB limit.
    internal static class NativeUpkeepFacts
    {
        // Every section reads one of four subsets of the visible spawned
        // things. One pass over the colony read's list fills all four
        //; each keeps the list's order, so every section's filter
        // and sort sees the same rows it saw when it scanned the list
        // itself. SharedPass off rebuilds the list and scans it per subset,
        // the independent path the ColonyFacts equality probe compares against.
        internal static bool SharedPass = true;

        internal sealed class ThingSets
        {
            internal readonly List<Thing> Items = new List<Thing>();
            internal readonly List<Building> PlayerBuildings = new List<Building>();

            internal static ThingSets Of(Map map, List<Thing> things)
            {
                var sets = new ThingSets();
                var player = Faction.OfPlayerSilentFail;
                if (!SharedPass) {
                    things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
                    sets.Items.AddRange(things.Where(t => t.def.category == ThingCategory.Item));
                    sets.PlayerBuildings.AddRange(things.OfType<Building>().Where(b => b.Faction == player));
                    return sets;
                }
                foreach (var t in things) {
                    if (t.def.category == ThingCategory.Item) sets.Items.Add(t);
                    if (t is Building b && b.Faction == player) sets.PlayerBuildings.Add(b);
                }
                return sets;
            }
        }

        // The Home census, read whole: a failed read throws and the upkeep section fails with it (#3771).
        internal static Obs.HomeCoverageFacts ReadHomeCoverage(Map map)
        {
            var home = map.areaManager.Home;
            var state = HomeCoverage.State(map);
            var targets = new List<Obs.HomeCoverageTarget>();
            foreach (var target in HomeCoverage.Targets(map).OrderBy(id => id, StringComparer.Ordinal)) {
                var full = HomeCoverage.FullScope(map, target);
                if (full == null) {
                    targets.Add(new Obs.HomeCoverageTarget.Unscoped(Id(target)));
                    continue;
                }
                var corridor = new List<DomainCell>();
                var enclosedInterior = new List<DomainCell>();
                var zone = new List<DomainCell>();
                var building = map.listerBuildings.allBuildingsColonist.ById(target);
                if (building != null) {
                    var footprint = new HashSet<IntVec3>(building.OccupiedRect());
                    foreach (var cell in full.Where(c => !footprint.Contains(c))) {
                        // Internal doors are the observed connections between enclosed rooms.
                        if (cell.GetEdifice(map) is Building_Door) corridor.Add(Spot(cell));
                        else enclosedInterior.Add(Spot(cell));
                    }
                } else {
                    // A stockpile's whole footprint, unbatched: colony extent takes every census stockpile.
                    zone.AddRange(full.OrderBy(c => c.x).ThenBy(c => c.z).Select(Spot));
                }
                targets.Add(new Obs.HomeCoverageTarget.Scoped(Id(target), new Obs.HomeExtentGeometry([.. enclosedInterior], [.. corridor], [.. zone])));
            }
            var settings = Find.PlaySettings ?? throw new InvalidOperationException("Find.PlaySettings is unavailable");
            return new Obs.HomeCoverageFacts {
                Revision = state.Revision, AutoHomeArea = settings.autoHomeArea,
                HomeCells = [.. home.ActiveCells.Select(Spot)], Targets = [.. targets]
            };
        }

        // The lighting census, read whole: a failed read throws and the upkeep section fails with it (#3831).
        // Work cells are the interaction cells of colonist benches (work tables and research benches): the cell a
        // pawn stands on while working, which is what RimWorld's darkness penalties measure.
        internal static Obs.LightingFacts ReadLighting(Map map, ThingSets sets)
        {
            var benches = sets.PlayerBuildings.Where(b => b.def.hasInteractionCell
                && (b is Building_WorkTable || b is Building_ResearchBench)).OrderBy(b => b.thingIDNumber);
            var lamps = sets.PlayerBuildings.Where(b => b.TryGetComp<CompGlower>() != null).OrderBy(b => b.thingIDNumber);
            return new Obs.LightingFacts {
                WorkCells = [.. benches.Select(b => {
                    var cell = b.InteractionCell;
                    return new Obs.WorkLightCell(Id(b.GetUniqueLoadID()), Spot(cell), Number(map.glowGrid.GroundGlowAt(cell)), cell.Roofed(map), [.. PlantDefs(map, cell)]);
                })],
                Lamps = [.. lamps.Select(b => new Obs.LampState(Id(b.GetUniqueLoadID()), b.TryGetComp<CompGlower>().Glows))]
            };
        }

        // The plants a proper room grows, standing or set in its growing zones: the brain reads which die to light
        // (cave fungus) off their def rows. Outside a proper room there are none.
        private static IEnumerable<string> PlantDefs(Map map, IntVec3 cell)
        {
            var room = cell.GetRoom(map);
            if (room == null || !room.ProperRoom) return Enumerable.Empty<string>();
            return room.Cells.SelectMany(c => new[] { c.GetPlant(map)?.def, (c.GetZone(map) as Zone_Growing)?.GetPlantDefToGrow() })
                .Where(d => d?.plant != null).Select(d => d!.defName).Distinct().OrderBy(n => n, StringComparer.Ordinal);
        }

        private static DomainCell Spot(IntVec3 c) => new DomainCell(c.x, c.z);

        internal static Obs.UpkeepFacts Populate(Map map, ThingSets sets, Obs.UpkeepFacts result, uint? routePathBudgetMs = null)
        {
            // The sections below rebind the captured `result` (records are immutable).
            void Read(string field, Action read)
            {
                var began = System.Diagnostics.Stopwatch.GetTimestamp();
                try { read(); }
                catch (Exception) { result = result with { Issues = [.. result.Issues, Issue(field, Common.UnavailableReason.ReadFailed, "Complete native upkeep section is unavailable.")] }; }
                ObservationWork.Detail("cf.upkeep." + field, System.Diagnostics.Stopwatch.GetTimestamp() - began);
            }
            Read("items", () => {
                // Every real map carries hundreds of natural chunk and slag
                // stacks that no upkeep concern may ever target, and a whole-map
                // census exceeded the bound on every real map. Items count
                // when the home area holds them (MaintainStorage debris),
                // when they sit in storage (reserve stock wherever it is
                // kept), or when they are the deteriorating, perishable or
                // medicine stacks SecureSupplies and medical/reserves
                // follow wherever they were dropped.
                // Corpses deteriorate and rot like food but are never
                // supplies to secure: a raider corpse across the map would
                // otherwise keep the SecureSupplies deficit open forever.
                // A settled stored stack (roofed, or not deteriorating) is no
                // target of any upkeep concern: only unstored rows and deteriorating
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
                        Item = NativeRef.Thing(t), Count = t.stackCount,
                        Forbidden = t.IsForbidden(Faction.OfPlayerSilentFail)
                    };
                    if (rot != null && rot.Active) value = value with { RotTicks = Math.Max(0, rot.TicksUntilRotAtCurrentTemp) };
                    return value;
                }).ToList();
                result = result with { Items = [.. result.Items, .. values] };
            });
            // The structure census is read whole: a failed read throws and fails the upkeep read (#3833).
            var structuresBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            // Non-damageable markers (including sleeping spots) have a
            // native -1 sentinel and cannot be repair targets.
            result = result with { Structures = [.. sets.PlayerBuildings.Where(b => b.def.useHitPoints).OrderBy(b => b.thingIDNumber).Select(b => new Obs.UpkeepStructure {
                Id = Id(b.GetUniqueLoadID()), Definition = Id(b.def.defName), Stuff = b.Stuff?.defName, Cell = Spot(b.Position),
                Home = b.OccupiedRect().All(c => map.areaManager.Home[c]),
                HitPoints = b.HitPoints, MaxHitPoints = b.MaxHitPoints
            })] };
            ObservationWork.Detail("cf.upkeep.structures", System.Diagnostics.Stopwatch.GetTimestamp() - structuresBegan);
            Read("flooring", () => {
                // Every proper indoor room the colony lives in (any cell in
                // the home area, not psychologically outdoors) with the
                // terrain under each cell; the catalog's terrain rows carry the
                // stats the rooms/flooring Need scores. A floor blueprint
                // or frame on a cell is reported as its pending terrain so
                // the planner never doubles an open order.
                var rooms = map.regionGrid.AllRooms.Where(r => r.ProperRoom && !r.PsychologicallyOutdoors && !r.TouchesMapEdge
                    && !r.Fogged && r.Cells.Any(c => map.areaManager.Home[c])).OrderBy(r => r.ID).ToList();
                var facts = new Obs.FlooringFacts();
                foreach (var r in rooms) {
                    var row = new Obs.FloorRoom { Room = NativeRef.Room(r) };
                    if (r.Role != null) row = row with { Role = Id(r.Role.defName) };
                    foreach (var c in r.Cells.OrderBy(c => c.z).ThenBy(c => c.x)) {
                        var terrain = c.GetTerrain(map);
                        var cell = new Obs.FloorCell { Cell = Cell(c), Terrain = Id(terrain.defName) };
                        var pending = c.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild is TerrainDef);
                        if (pending != null) cell = cell with { Pending = Id(pending.def.entityDefToBuild.defName) };
                        row = row with { Cells = [.. row.Cells, cell] };
                    }
                    facts = facts with { Rooms = [.. facts.Rooms, row] };
                }
                result = result with { Flooring = new Obs.FlooringSection { Outcome = facts } };
            });
            Read("routes", () => {
                // Every facility a colonist must reach, with each mobile
                // colonist's native reachability from where they stand and
                // the cost of the path the game itself would walk (every
                // reachable pair, unless the brain's route_path_budget_ms runs out:
                // later pairs then report path_skipped, never a silent
                // absence). A facility no colonist reaches
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
                facts = facts with { PawnIds = [.. facts.PawnIds, .. people.Select(p => Id(p.GetUniqueLoadID()))] };
                var measured = 0; long pathTicks = 0, reachTicks = 0;
                var skipped = 0;
                var pathBudgetTicks = routePathBudgetMs.HasValue ? routePathBudgetMs.Value * System.Diagnostics.Stopwatch.Frequency / 1000 : long.MaxValue;
                Obs.RouteFacility Facility(Common.Ref reference, Obs.RouteFacilityKind kind, IntVec3 cell, Func<Pawn, bool> reaches, Func<Pawn, LocalTargetInfo> target, PathEndMode mode)
                {
                    var row = new Obs.RouteFacility { Facility = reference, Kind = kind, Cell = Cell(cell) };
                    var room = cell.GetRoom(map);
                    if (room != null && room.ProperRoom && !room.PsychologicallyOutdoors) row = row with { Room = NativeRef.Room(room) };
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
                        if (travel.Reachable.GetValueOrDefault())
                        {
                            anyReach = true;
                            if (pathTicks >= pathBudgetTicks)
                            {
                                skipped++;
                                travel = travel with { PathSkipped = true };
                            }
                            else
                            {
                                measured++;
                                var pf0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                using (var path = map.pathFinder.FindPathNow(p.Position, target(p), TraverseParms.For(p, Danger.Some), peMode: mode))
                                {
                                    if (path.Found)
                                    {
                                        travel = travel with { PathCost = checked((int)Math.Min(path.TotalCost, int.MaxValue)) }; travel = travel with { PathCells = path.NodesLeftCount };
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
                        row = row with { Travel = [.. row.Travel, travel] };
                    }
                    if (anyReach)
                    {
                        foreach (var (bc, door) in crossed.Values)
                            row = row with { Breaches = [.. row.Breaches, new Obs.RouteBreach { Cell = Cell(bc), Edifice = Id(door.def.defName), Pending = Id(door.def.entityDefToBuild.defName), Distance = 0 }] };
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
                        foreach (var (bc, edifice, pending, distance) in breaches.OrderBy(b => b.distance).ThenBy(b => b.cell.z).ThenBy(b => b.cell.x))
                        {
                            var breach = new Obs.RouteBreach { Cell = Cell(bc), Edifice = Id(edifice), Distance = distance };
                            if (pending != null) breach = breach with { Pending = Id(pending) };
                            row = row with { Breaches = [.. row.Breaches, breach] };
                        }
                    }
                    return row;
                }
                foreach (var (thing, kind, cell) in facilities)
                {
                    var mode = thing.def.hasInteractionCell ? PathEndMode.InteractionCell : PathEndMode.Touch;
                    facts = facts with { Facilities = [.. facts.Facilities, Facility(NativeBuildingObservationTools.Ref(thing), kind, cell, p => !thing.IsForbidden(p) && p.CanReach(thing, mode, Danger.Some), p => thing, mode)] };
                }
                var stockpiles = map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderBy(z => z.ID).ToList();
                foreach (var zone in stockpiles)
                {
                    var cell = zone.Cells.OrderBy(c => c.z).ThenBy(c => c.x).FirstOrDefault(c => c.Standable(map));
                    if (cell == default) continue;
                    var reference = new Common.Ref { Id = Id("zone-" + zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
                    facts = facts with { Facilities = [.. facts.Facilities, Facility(reference, Obs.RouteFacilityKind.Stockpile, cell, p => p.CanReach(cell, PathEndMode.OnCell, Danger.Some), p => cell, PathEndMode.OnCell)] };
                }
                var traffic = map.GetComponent<TrafficState>();
                if (traffic != null)
                {
                    // Every nonzero cell of each layer, busiest first;
                    // traffic_samples is the colonist layer's decayed total.
                    facts = facts with { TrafficSamples = traffic.Counts.Total(TrafficCounts.Colonist) };
                    for (int layer = 0; layer < TrafficCounts.Layers; layer++)
                        foreach (var index in traffic.Counts.Nonzero(layer))
                        {
                            var at = map.cellIndices.IndexToCell(index);
                            var cell = new Obs.TrafficCell { Cell = Cell(at), Samples = traffic.Counts[layer, index], Terrain = Id(at.GetTerrain(map).defName), Home = map.areaManager.Home[at], Layer = (Obs.TrafficLayer)(layer + 1) };
                            var pending = at.GetThingList(map).FirstOrDefault(t => (t is Blueprint || t is Frame) && t.def.entityDefToBuild is TerrainDef);
                            if (pending != null) cell = cell with { Pending = Id(pending.def.entityDefToBuild.defName) };
                            facts = facts with { Traffic = [.. facts.Traffic, cell] };
                        }
                }
                ObservationWork.Detail("cf.upkeep.routes.path", pathTicks, measured + skipped);
                ObservationWork.Detail("cf.upkeep.routes.reach", reachTicks);
                result = result with { Routes = new Obs.RoutesSection { Outcome = facts } };
            });
            Read("people", () => {
                var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                Obs.UpkeepPerson Person(Pawn p) => new Obs.UpkeepPerson {
                    Pawn = NativePawnObservationTools.Ref(p), OwnedBed = NativeRef.Of(p.ownership?.OwnedBed),
                    ComfortableMinC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMax))
                };
                var values = people.Select(Person).ToList();
                for (var i = 0; i < people.Count; i++) values[i] = SleepingRelations(people[i], values[i]);
                result = result with { People = [.. result.People, .. values] };
                // Slaves of the colony: shelter/sleeping gives each a
                // bed set for slaves.
                result = result with { Slaves = [.. result.Slaves, .. map.mapPawns.AllPawnsSpawned.Where(p => p.IsSlaveOfColony && !p.Dead).OrderBy(p => p.thingIDNumber).Select(Person)] };
                // Hosted guests: housed to their title like a colonist;
                // a visiting envoy is not hosted and stays out.
                var guests = map.mapPawns.AllPawnsSpawned.Where(HostedGuest).OrderBy(p => p.thingIDNumber).ToList();
                var guestRows = guests.Select(Person).ToList();
                for (var i = 0; i < guests.Count; i++) guestRows[i] = SleepingRelations(guests[i], guestRows[i]);
                result = result with { Guests = [.. result.Guests, .. guestRows] };
            });
            Read("beds", () => {
                var beds = sets.PlayerBuildings.OfType<Building_Bed>().OrderBy(b => b.thingIDNumber).ToList();
                var people = map.mapPawns.AllPawnsSpawned.Where(p => (p.IsFreeColonist || p.IsSlaveOfColony || HostedGuest(p)) && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                var values = beds.Select(b => {
                    var row = new Obs.UpkeepBed { Bed = NativeBuildingObservationTools.Ref(b),
                        Medical = b.Medical, Prisoners = b.ForPrisoners, ForSlaves = b.ForSlaves, Roofed = b.OccupiedRect().All(c => c.Roofed(map)),
                        TemperatureC = Number(b.AmbientTemperature),
                        // Every spawned bed stands in a room (outdoors is one); none is a broken read.
                        Room = (b.GetRoom() ?? throw new InvalidOperationException("Bed " + b.GetUniqueLoadID() + " stands in no room.")).ID.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    if (b.TryGetQuality(out var quality)) row = row with { Quality = (Defs.QualityCategory)(int)quality }; if (b.Stuff != null) row = row with { Stuff = b.Stuff.defName };
                    var owners = b.OwnersForReading.Select(p => Id(p.GetUniqueLoadID())).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    row = row with { Owners = [.. row.Owners, .. NativeRef.All(owners)] };
                    row = row with { Users = [.. row.Users, .. NativeRef.All(people.Where(p => p.CurrentBed() == b).Select(p => Id(p.GetUniqueLoadID())))] };
                    row = row with { AccessibleTo = [.. row.AccessibleTo, .. NativeRef.All(people.Where(p => !b.IsForbidden(p) && p.CanReach(b, PathEndMode.OnCell, Danger.None)).Select(p => Id(p.GetUniqueLoadID())))] };
                    return row;
                }).ToList();
                result = result with { Beds = [.. result.Beds, .. values] };
            });
            Read("animals", () => {
                var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                    && p.Faction == Faction.OfPlayerSilentFail).OrderBy(p => p.thingIDNumber).ToList();
                var values = animals.Select(p => {
                    var requiresPen = AnimalPenUtility.NeedsToBeManagedByRope(p);
                    var suitable = requiresPen ? AnimalPenUtility.ClosestSuitablePen(p, false) : null;
                    // The herd facts (pen, training, area, care) ride the
                    // pawn table row's animal state.
                    var value = new Obs.AnimalFeed {
                        Pawn = NativePawnObservationTools.Ref(p),
                        RequiresPen = requiresPen
                    };
                    if (suitable != null) value = value with { SuitablePen = NativeRef.Of(Id(suitable.parent.GetUniqueLoadID())) };
                    // A rope-managed animal is a roamer (NeedsToBeManagedByRope tests Pawn.Roamer), so its RoamMtbDays is set.
                    if (requiresPen) value = value with { Roam = new Obs.AnimalRoam { Race = Id(p.def.defName),
                        MtbDays = Number(p.RoamMtbDays ?? throw new InvalidOperationException("A rope-managed animal has no RoamMtbDays.")),
                        MarketValue = Number(p.MarketValue), ReachesMapEdge = p.CanReachMapEdge() } };
                    return value;
                }).ToList();
                result = result with { Animals = [.. result.Animals, .. values] };
            });
            // The factionless animals an animals/herd tame write can target:
            // native tame eligibility only, no feed or pen facts. Every
            // spawned wild animal is listed: the census has no row bound.
            Read("wild_animals", () => {
                var wild = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal && p.Faction == null)
                    .OrderBy(p => p.thingIDNumber).ToList();
                result = result with { WildAnimals = [.. result.WildAnimals, .. wild.Select(p => new Obs.AnimalFeed {
                    Pawn = NativePawnObservationTools.Ref(p), RequiresPen = false })] };
            });
            return result;
        }

        // SleepingRelations fills a colonist's partners (lover, spouse and
        // fiance relations to living pawns on the same map), the native
        // willingness to share a bed with each of them (the plain SharedBed
        // precept when there is none), the most senior royal title and what the pawn waives of its requirements.
        // A hosted guest: a humanlike the player faction holds as a guest
        // (quest lodgers included), not a prisoner.
        internal static bool HostedGuest(Pawn p) => p.Spawned && !p.Dead && p.RaceProps.Humanlike && !p.IsPrisoner
            && p.HostFaction == Faction.OfPlayerSilentFail && p.guest?.GuestStatus == GuestStatus.Guest;

        private static Obs.UpkeepPerson SleepingRelations(Pawn p, Obs.UpkeepPerson row)
        {
            var partners = (p.relations?.DirectRelations ?? new List<DirectPawnRelation>())
                .Where(r => LovePartnerRelationUtility.IsLovePartnerRelation(r.def)
                    && r.otherPawn != null && !r.otherPawn.Dead && r.otherPawn.Spawned && r.otherPawn.Map == p.Map)
                .Select(r => r.otherPawn).Distinct().OrderBy(o => o.thingIDNumber).ToList();
            row = row with { Partners = [.. row.Partners, .. NativeRef.All(partners.Select(o => Id(o.GetUniqueLoadID())))] };
            row = row with { BedSharingAllowed = partners.Count == 0 ? IdeoUtility.DoerWillingToDo(HistoryEventDefOf.SharedBed, p) : partners.All(o => BedUtility.WillingToShareBed(p, o)) };
            if (!ModsConfig.RoyaltyActive) return row;
            var title = p.royalty?.MostSeniorTitle?.def;
            if (title == null) return row;
            // The title's requirements are its RoyalTitleDef row; what the
            // pawn waives is its own state: the Ascetic trait drops them all and
            // an ideoligion precept can disable one.
            row = row with { RoyalTitle = Id(title.defName) };
            row = row with { Ascetic = p.story?.traits?.HasTrait(TraitDefOf.Ascetic) == true };
            if (p.Ideo != null) row = row with { Precepts = [.. row.Precepts, .. p.Ideo.PreceptsListForReading.Select(x => Id(x.def.defName)).Distinct().OrderBy(d => d, StringComparer.Ordinal)] };
            return row;
        }
    }
}
