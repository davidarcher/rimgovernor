#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Policy;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;
using Domain = RimGovernor.Brain.Domain;

namespace HomeBridge.BridgeTools;

// Private disposable acceptance only (#3341): the main-thread time of the forecast's native reads on a large colony.
// 32 free colonists, five planned rooms (one with a 144-cell floor), twenty wood stacks, eight meal stacks and eight beds
// are injected; the planned-site read, the route read between the rooms' sites, the needs read (with its #3339 roof,
// thought-stage and deterioration reads), the coupled read and the closed-room prediction read (#3339) are each timed per call. The meals and beds are needs targets, so the coupled read has locations to route between.
// Nothing is placed or ordered.
public sealed partial class PawnControlFixture
{
    private const int LargeColonists = 32;
    private static IntVec3 largeColonyOrigin;

    [Tool("test/large_colony_reads_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Grow the colony to 32 free colonists and clear one open 34x24 patch for five planned rooms and twenty wood stacks.")]
    public async Task<object> LargeColonyReadsPrepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var anchor = map.mapPawns.FreeColonistsSpawned.First();
            const int width = 34, height = 24;
            largeColonyOrigin = GenRadial.RadialCellsAround(anchor.Position, 50, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.GetTerrain(map).IsWater
                    && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.Roofed(map)
                    && !cell.GetThingList(map).Any(t => t.def.category == Verse.ThingCategory.Building))
                && anchor.CanReach(c, PathEndMode.Touch, Danger.None));
            if (largeColonyOrigin == default) throw new InvalidOperationException("No open reachable 34x24 patch.");
            var patch = new CellRect(largeColonyOrigin.x, largeColonyOrigin.z, width, height);
            foreach (var cell in patch.Cells)
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category is Verse.ThingCategory.Item or Verse.ThingCategory.Filth).ToList()) thing.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            var generated = 0;
            while (map.mapPawns.FreeColonistsSpawned.Count < LargeColonists)
            {
                var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    forceGenerateNewPawn: true, colonistRelationChanceFactor: 0f, allowDead: false, allowDowned: false,
                    canGeneratePawnRelations: false, fixedBiologicalAge: 30f, fixedChronologicalAge: 30f));
                var cell = CellFinder.RandomClosewalkCellNear(anchor.Position, map, 12, c => c.Standable(map) && !c.Fogged(map) && !patch.Contains(c));
                GenSpawn.Spawn(pawn, cell, map);
                if (++generated > LargeColonists * 2) throw new InvalidOperationException("Could not grow the colony to " + LargeColonists + ".");
            }
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToArray()) p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 120000), JobCondition.InterruptForced);
            return new { success = true, colonists = map.mapPawns.FreeColonistsSpawned.Count, generated, origin = new { x = largeColonyOrigin.x, z = largeColonyOrigin.z } };
        }, ct).ConfigureAwait(false);

    private static IntVec3 LargeAt(int x, int z) => new(largeColonyOrigin.x + x, 0, largeColonyOrigin.z + z);

    // One main-thread call holding every read so the world does not move between them; each read is timed alone.
    // Every read runs twice: the first is cold (pathfinder and def caches), the second warm.
    [Tool("test/large_colony_reads", Description = "UNSAFE FOR MODEL EXECUTION. On the prepared patch spawn twenty wood stacks, eight meal stacks and eight beds, then time the planned-site, planned-route, needs and coupled native reads for five planned rooms and every colonist.")]
    public async Task<object> LargeColonyReads(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (largeColonyOrigin == default) throw new InvalidOperationException("Prepare large_colony_reads first.");
            var map = Find.CurrentMap;
            var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            if (colonists.Count < 30) throw new InvalidOperationException("Only " + colonists.Count + " free colonists.");
            for (var i = 0; i < 20; i++)
            {
                var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = wood.def.stackLimit;
                GenSpawn.Spawn(wood, LargeAt(32 + i % 2, 2 + i), map);
            }
            // Needs targets east of the 12x12 room: eight meal stacks and eight player beds, apart from each other.
            for (var i = 0; i < 8; i++)
            {
                var meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                meal.stackCount = 10;
                GenSpawn.Spawn(meal, LargeAt(17 + i % 4 * 3, 8 + i / 4 * 2), map);
                var bed = ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                bed.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(bed, LargeAt(17 + i % 4 * 3, 13 + i / 4 * 4), map, Rot4.North);
            }
            var context = Context();
            var identity = context.Identity!;

            Cell C(IntVec3 cell) => new(cell.x, cell.z);
            var floor = DefDatabase<TerrainDef>.GetNamed("WoodPlankFloor");
            Amount[] Priced(List<ThingDefCountClass>? costs, int pieces) =>
                (costs ?? new()).Select(c => new Amount(new Resource(c.thingDef.defName), (long)c.count * pieces)).ToArray();
            PlannedBuildSite Site(int room, string kind, OpKind op, string def, string stuff, Cell[] cells, Amount[] material) =>
                new("planned/room" + room + "/" + kind, room, op, def, stuff, Domain.Rotation.North, Domain.EquatableArray.From(cells))
                    { Material = Domain.EquatableArray.From(material) };
            // A room: a wall ring (less the door, south middle), the door, the interior's plank floor and its roof.
            IEnumerable<PlannedBuildSite> Room(int room, int x, int z, int size)
            {
                var door = LargeAt(x + size / 2, z);
                var walls = new CellRect(largeColonyOrigin.x + x, largeColonyOrigin.z + z, size, size).EdgeCells.Where(c => c != door).Select(C)
                    .OrderBy(c => c.X).ThenBy(c => c.Z).ToArray();
                var interior = new CellRect(largeColonyOrigin.x + x + 1, largeColonyOrigin.z + z + 1, size - 2, size - 2).Cells.Select(C)
                    .OrderBy(c => c.X).ThenBy(c => c.Z).ToArray();
                yield return Site(room, "wall/WoodLog", OpKind.WallIn, "Wall", "WoodLog", walls, Priced(ThingDefOf.Wall.CostListAdjusted(ThingDefOf.WoodLog), walls.Length));
                yield return Site(room, "door/WoodLog", OpKind.DoorIn, "Door", "WoodLog", [C(door)], Priced(ThingDefOf.Door.CostListAdjusted(ThingDefOf.WoodLog), 1));
                yield return Site(room, "floor/" + floor.defName, OpKind.FloorIn, floor.defName, "", interior, Priced(floor.CostList, interior.Length));
                yield return Site(room, "roof", OpKind.RoofIn, "", "", interior, []);
            }
            // Four 3x3 rooms along the south and one 12x12 room (144 floor cells, 51 wall cells) to their north.
            var sites = Enumerable.Range(0, 4).SelectMany(i => Room(i + 1, 1 + i * 7, 1, 5)).Concat(Room(5, 1, 8, 14)).ToList();

            static double Ms(Stopwatch w) => Math.Round(w.Elapsed.TotalMilliseconds, 2);
            T Timed<T>(Func<T> read, List<double> into)
            {
                var watch = Stopwatch.StartNew();
                var value = read();
                into.Add(Ms(watch));
                return value;
            }

            // The production reads (Rounder.PlannedSitesAsync, the colony observation): one planned-site read, one route
            // read, one needs read and one coupled read, each with its own leg cache as in production.
            var asked = PolicyFunctions.PlannedSiteRequest(sites);
            var siteMs = new List<double>();
            Obs.ConstructionActivityRead siteRead = null!;
            var siteSearches = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                var legs = new NativePathLegs(map);
                siteRead = Timed(() => NativeConstructionActivity.Read(new(identity, default) { Planned = asked }, legs), siteMs);
                siteSearches = legs.Searches;
            }
            var joined = PolicyFunctions.JoinPlannedSites(sites, siteRead);
            var probes = PolicyFunctions.PlannedRouteProbes(joined.Sites);
            var routeMs = new List<double>();
            Obs.ConstructionActivityRead routeRead = null!;
            var routeSearches = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                var legs = new NativePathLegs(map);
                routeRead = Timed(() => NativeConstructionActivity.Read(new(identity, default) { Routes = probes }, legs), routeMs);
                routeSearches = legs.Searches;
            }
            var facts = PolicyFunctions.JoinPlannedRoutes(joined, probes, routeRead);
            // The closed-room prediction read (#3339): the five rooms closed by their wood rings, as Rounder.PlannedShelterReadsAsync asks it.
            var plannedRooms = Enumerable.Range(0, 4).Select(i => new PlannedRoom { Id = i + 1, Region = PolicyFunctions.RectRegion(new(largeColonyOrigin.x + 2 + i * 7, largeColonyOrigin.z + 2, 3, 3)) })
                .Append(new PlannedRoom { Id = 5, Region = PolicyFunctions.RectRegion(new(largeColonyOrigin.x + 2, largeColonyOrigin.z + 9, 12, 12)) }).ToList();
            var shapes = PolicyFunctions.PlannedRoomPredictionRequestOf(plannedRooms, sites);
            var predictionMs = new List<double>();
            Obs.ConstructionActivityRead predictionRead = null!;
            for (var pass = 0; pass < 2; pass++)
                predictionRead = Timed(() => NativeConstructionActivity.Read(new(identity, default) { RoomPredictions = shapes }), predictionMs);
            var needsMs = new List<double>();
            Obs.NeedsForecastRead needs = null!;
            for (var pass = 0; pass < 2; pass++) needs = Timed(() => NativeNeedsForecast.Read(map, colonists), needsMs);
            var coupledMs = new List<double>();
            Obs.CoupledForecastRead coupled = null!;
            for (var pass = 0; pass < 2; pass++) coupled = Timed(() => NativeCoupledForecast.Read(map, context, needs), coupledMs);

            var ids = colonists.Select(p => p.GetUniqueLoadID()).ToHashSet();
            var siteRows = facts.Sites.SelectMany(s => s.Options).ToArray();
            var answered = facts.Sites.Count(s => s.Op != OpKind.RoofIn && s.Verdict == Obs.PlannedSiteVerdict.Feasible);
            var siteBuilders = siteRows.Select(o => o.Pawn).Distinct().Count();
            var needsPawns = needs.Pawns.Select(p => p.Pawn).Where(ids.Contains).Distinct().Count();
            var routePawns = facts.Routes.Select(r => r.Pawn).Distinct().Count();
            var coupledRoutePawns = coupled.Routes.Select(r => r.Pawn).Where(ids.Contains).Distinct().Count();
            if (answered != sites.Count(s => s.Op != OpKind.RoofIn) || needsPawns != colonists.Count || !siteRead.Known || !routeRead.Known
                || routePawns != colonists.Count || coupledRoutePawns != colonists.Count
                || shapes.Count != 5 || predictionRead.RoomPredictions.Count != 5 || predictionRead.RoomPredictions.Any(r => !r.Stage.IsKnown))
                throw new InvalidOperationException("Incomplete reads: feasible sites " + answered + ", needs pawns " + needsPawns + "/" + colonists.Count
                    + ", route pawns " + routePawns + ", coupled route pawns " + coupledRoutePawns + ", room predictions " + string.Join(";", predictionRead.RoomPredictions) + "; findings " + string.Join(",", facts.Findings));
            return new
            {
                success = true,
                colonists = colonists.Count,
                sites = sites.Count, siteRows = siteRows.Length, siteBuilders, siteSearches,
                largestSite = sites.Max(s => s.Positions.Count),
                planned_site_ms = siteMs,
                probes = probes.Count, routes = facts.Routes.Count, routePawns, routeSearches,
                planned_route_ms = routeMs,
                needsPawns, needsTargets = needs.Pawns.Sum(p => p.Targets.Count),
                needs_ms = needsMs,
                coupledRoutes = coupled.Routes.Count, coupledRoutePawns,
                coupledLocations = coupled.Routes.Where(r => !r.FromStart).Select(r => r.From).Distinct().Count(),
                coupled_ms = coupledMs,
                roomPredictions = predictionRead.RoomPredictions.Select(r => new { r.Room, r.Role, stage = r.Stage.Value, r.Cells }).ToArray(),
                room_prediction_ms = predictionMs,
                needsRoofReads = new { weather = needs.WeatherThought, rain = needs.RainRate.IsKnown, cells = needs.Pawns.Sum(p => p.Targets.Count(t => t.Cell != null)),
                    decaying = needs.Pawns.Sum(p => p.Targets.Count(t => t.Deterioration != null)), stages = needs.Pawns.Sum(p => p.ThoughtStages.Count) },
                round_ms = Enumerable.Range(0, 2).Select(i => Math.Round(siteMs[i] + routeMs[i] + needsMs[i] + coupledMs[i] + predictionMs[i], 2)).ToArray(),
                findings = facts.Findings.ToArray(),
                limitations = "Times one lab map's reads per call on the main thread (cold then warm); the coupled read's time includes its own construction, food, recipe, thermal, support, practice, hunt and route sub-reads, with the needs read passed in. A round makes each read once.",
            };
        }, ct).ConfigureAwait(false);
}
