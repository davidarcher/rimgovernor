#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

namespace HomeBridge.BridgeTools;

// Private disposable acceptance only: the leg store's invalidation (#3686). On an open 40x40 soil patch a leg runs 30
// cells east; walls are built off its path, across it and removed again, and a target cell is walled in and opened,
// each change followed by a region rebuild, one tick and a fresh read of the leg with its search count. Nothing is ordered.
public sealed partial class PawnControlFixture
{
    [Tool("test/leg_invalidation", Description = "UNSAFE FOR MODEL EXECUTION. Build and remove walls on an open patch and read a stored path leg after each change, with its search count.")]
    public async Task<object> LegInvalidation(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var pawn = map.mapPawns.FreeColonistsSpawned.First();
            const int side = 40;
            var origin = GenRadial.RadialCellsAround(pawn.Position, 60, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, side, side).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && cell.GetEdifice(map) == null && !cell.GetTerrain(map).IsWater
                    && cell.GetTerrain(map).passability != Traversability.Impassable
                    && !cell.GetThingList(map).Any(t => t.def.category == Verse.ThingCategory.Building || t is Pawn))
                && pawn.CanReach(c, PathEndMode.OnCell, Danger.None));
            if (origin == default) throw new InvalidOperationException("No open reachable 40x40 patch.");
            foreach (var cell in new CellRect(origin.x, origin.z, side, side).Cells)
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category is Verse.ThingCategory.Item or Verse.ThingCategory.Filth).ToList()) thing.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            foreach (var p in map.mapPawns.FreeColonistsSpawned.ToArray()) p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 120000), JobCondition.InterruptForced);
            IntVec3 At(int x, int z) => new(origin.x + x, 0, origin.z + z);
            var from = At(5, 20);
            var to = At(35, 20);
            var walledIn = At(20, 36);
            var regionChanges = 0;
            void Counted() => regionChanges++;
            map.events.RegionsRoomsChanged += Counted;
            var steps = new List<Fx.LegInvalidationStep>();
            try
            {
                Thing Wall(IntVec3 cell)
                {
                    var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                    wall.SetFaction(Faction.OfPlayer);
                    return GenSpawn.Spawn(wall, cell, map);
                }
                void Read(string step, IntVec3 target)
                {
                    // The finder gathers its map data's changes once per tick (PathFinderMapData.GatherData), so the
                    // change is ticked past before the read, as a live read always is.
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    Find.TickManager.DoSingleTick();
                    var legs = new NativePathLegs(map);
                    var cost = legs.Cost(pawn, from, target, PathEndMode.OnCell);
                    using var path = map.pathFinder.FindPathNow(from, target, TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
                    steps.Add(new Fx.LegInvalidationStep { Step = step, Searches = legs.Searches, Cost = cost, Search = path.Found ? path.TotalCost : null, RegionChanges = regionChanges });
                    regionChanges = 0;
                }
                Read("open", to);
                // Off the path: from the leg's ends to (20, 2) and back is 30 diagonal and 6 cardinal steps, over its 30.
                Wall(At(20, 2));
                Read("wall-off-path", to);
                // Across the path: a line at x 20 from z 10 to 30, so the leg goes round an end.
                var line = Enumerable.Range(10, 21).Select(z => Wall(At(20, z))).ToList();
                Read("detour", to);
                foreach (var wall in line) wall.Destroy(DestroyMode.Vanish);
                Read("detour-removed", to);
                var ring = GenAdj.AdjacentCellsAround.Select(offset => Wall(walledIn + offset)).ToList();
                Read("walled-in", walledIn);
                ring[0].Destroy(DestroyMode.Vanish);
                Read("opened", walledIn);
            }
            finally
            {
                map.events.RegionsRoomsChanged -= Counted;
            }
            return FixtureKit.Reply(new Fx.LegInvalidationRead { Steps = steps });
        }, ct).ConfigureAwait(false);
}
