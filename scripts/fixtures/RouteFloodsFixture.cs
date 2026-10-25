#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Native;

// Private disposable acceptance only: the coupled forecast's route matrix by per-origin floods (#3685) against the
// game's A* search. Three wall lines with gaps go across the lab, about two hundred reachable cells on a six-cell grid
// over it are flooded, each to every other, and a sample of the pairs is searched by FindPathNow with the same pawn and
// TraverseParms. Nothing is ordered.
public sealed partial class PawnControlFixture
{
    [Tool("test/route_floods", Description = "UNSAFE FOR MODEL EXECUTION. Wall the lab in three lines, flood about two hundred cells each to every other, and search a sample of the pairs by A*.")]
    public async Task<object> RouteFloods(INativeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var pawn = map.mapPawns.FreeColonistsSpawned.First();
            var size = map.Size;
            // The walls: two across with a gap at opposite ends and one between them, so many legs detour.
            var walls = new List<IntVec3>();
            for (var x = size.x / 10; x < size.x * 7 / 10; x++) walls.Add(new IntVec3(x, 0, size.z * 3 / 10));
            for (var x = size.x * 3 / 10; x < size.x * 9 / 10; x++) walls.Add(new IntVec3(x, 0, size.z * 6 / 10));
            for (var z = size.z * 3 / 10 + 5; z < size.z * 6 / 10 - 5; z++) walls.Add(new IntVec3(size.x / 2, 0, z));
            foreach (var cell in walls.Where(c => c.InBounds(map) && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t is Pawn)))
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t.def.category is Verse.ThingCategory.Plant or Verse.ThingCategory.Item or Verse.ThingCategory.Filth).ToList()) thing.Destroy();
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(wall, cell, map);
            }
            var cells = new List<IntVec3>();
            for (var x = 4; x < size.x - 4; x += 6)
            for (var z = 4; z < size.z - 4; z += 6)
            {
                var cell = new IntVec3(x, 0, z);
                if (cell.Standable(map) && !cell.Fogged(map) && pawn.CanReach(cell, PathEndMode.OnCell, Danger.None)) cells.Add(cell);
            }
            cells = cells.Take(200).ToList();
            if (cells.Count < 100) throw new InvalidOperationException("Only " + cells.Count + " reachable cells on the grid.");
            // The matrix: every cell to every other, flooded for the one pawn, as the coupled forecast floods its locations.
            var legs = new NativePathLegs(map);
            var started = Stopwatch.GetTimestamp();
            legs.Floods(cells.Select(from => (pawn, from, (IReadOnlyList<IntVec3>)cells.Where(to => to != from).ToList())).ToList());
            var matrixMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            var parms = TraverseParms.For(pawn, Danger.None);
            var pairs = new List<Fx.RouteFloodPair>();
            for (var k = 0; k < 250; k++)
            {
                var from = cells[k % cells.Count];
                var to = cells[(k * 7 + 3) % cells.Count];
                if (from == to) continue;
                var flood = legs.Cost(pawn, from, to, PathEndMode.OnCell);
                using var path = map.pathFinder.FindPathNow(from, to, parms, peMode: PathEndMode.OnCell);
                var diagonal = 0;
                if (path.Found)
                {
                    var nodes = path.NodesReversed;
                    for (var i = 1; i < nodes.Count; i++)
                        if (nodes[i].x != nodes[i - 1].x && nodes[i].z != nodes[i - 1].z) diagonal++;
                }
                pairs.Add(new Fx.RouteFloodPair { Leg = $"({from.x}, {from.z}) -> ({to.x}, {to.z})", Flood = flood, Search = path.Found ? path.TotalCost : null, Diagonal = diagonal });
            }
            return FixtureKit.Reply(new Fx.RouteFloodsRead { Origins = cells.Count, Floods = legs.FloodCount, MatrixMs = matrixMs, Pairs = pairs });
        }, ct).ConfigureAwait(false);
}
