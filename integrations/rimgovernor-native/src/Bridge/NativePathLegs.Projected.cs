using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Unity.Collections;
using Verse;
using Verse.AI;
using Shape = RimGovernor.Brain.GameOrders.Observations.PlannedRoomShape;

namespace RimGovernor.Native;

internal sealed partial class NativePathLegs
{
    internal Projected Project(Pawn pawn, Shape shape, IEnumerable<Shape> layout) => new(this, pawn, shape, layout);

    /// <summary>A closed room over private copies of the native path job's geometry. The same PathGridJob and
    /// PathFinderJob price terrain, danger, allowed areas, door opening, pawn blocking and diagonal movement.
    /// Projected legs never enter the live-map cache and nothing is spawned or written to the map.</summary>
    internal sealed class Projected : IDisposable
    {
        private readonly Map map;
        private readonly Pawn pawn;
        private readonly FinderAccess access;
        private readonly Dictionary<IntVec3, ThingDef> pieces = new();
        private readonly Dictionary<IntVec3, Building_Door> doors = new();
        private readonly Dictionary<IntVec3, TerrainDef> floors = new();
        private NativeArray<int> direct;
        private NativeArray<CellConnection> connectivity;
        private NativeBitArray buildings, fences;
        private PathFinder.GridJobOutput output;
        private FloodChain chain;
        private PathFinderJob job;
        private readonly PathFinder.MapGridRequest query;

        internal Projected(NativePathLegs owner, Pawn pawn, Shape shape, IEnumerable<Shape> layout)
        {
            map = owner.map;
            this.pawn = pawn;
            access = FinderAccess.Instance ?? throw new InvalidOperationException("Projected routes require the native path grid and door parameterization.");
            var parms = TraverseParms.For(pawn, Danger.None);
            using var request = map.pathFinder.CreateRequest(pawn.Position, pawn.Position, null, parms, null, PathEndMode.OnCell, pawn);
            // Complete/gather native data before taking private inputs, just as the live leg owner does.
            _ = access.Grid(map.pathFinder, request);
            query = PathFinder.MapGridRequest.For(request);
            var geometry = layout.Where(s => s.Room != shape.Room).GroupBy(s => s.Room).Select(g => g.First()).Append(shape).ToArray();
            foreach (var piece in geometry.SelectMany(s => s.Pieces))
            {
                var at = new IntVec3(piece.Cell.X, 0, piece.Cell.Z);
                if (!at.InBounds(map)) throw new InvalidOperationException("Projected route piece is outside the map.");
                if (PlacementPreviewOperation.TryResolveBuildable(piece.Definition, out var def, out _) && def is TerrainDef terrain) floors[at] = terrain;
                else Add(at, def as ThingDef ?? throw new InvalidOperationException("Projected route piece is unresolved: " + piece.Definition), piece.Stuff);
            }
            foreach (var furniture in geometry.SelectMany(s => s.Furniture))
            {
                if (!PlacementPreviewOperation.TryResolveBuildable(furniture.Definition, out var buildable, out _) || buildable is not ThingDef def)
                    throw new InvalidOperationException("Projected route furniture is unresolved: " + furniture.Definition);
                foreach (var at in GenAdj.OccupiedRect(new(furniture.Cell.X, 0, furniture.Cell.Z), new Rot4((int)furniture.Rotation), def.size)) Add(at, def, furniture.Stuff);
            }
            var count = map.cellIndices.NumGridCells;
            try
            {
                chain = new FloodChain(count);
                output.Initalize(count);
                var gridJob = new PathGridJob();
                var gridQuery = query;
                map.pathFinder.MapData.ParameterizeGridJob(request, ref gridQuery, ref gridJob, ref output);
                direct = new NativeArray<int>(count, Allocator.Persistent);
                buildings = new NativeBitArray(count, Allocator.Persistent);
                fences = new NativeBitArray(count, Allocator.Persistent);
                for (var i = 0; i < count; i++)
                {
                    direct[i] = gridJob.pathGridDirect[i];
                    buildings.Set(i, gridJob.building.IsSet(i));
                    fences.Set(i, gridJob.fence.IsSet(i));
                }
                foreach (var at in pieces.Keys.Concat(floors.Keys).Distinct())
                {
                    var i = map.cellIndices.CellToIndex(at);
                    pieces.TryGetValue(at, out var def);
                    direct[i] = StaticCost(at, def);
                    if (def is not null)
                    {
                        buildings.Set(i, def.Fillage == FillCategory.Full);
                        fences.Set(i, def.IsFence);
                    }
                }
                gridJob.pathGridDirect = direct.AsReadOnly();
                gridJob.building = buildings.AsReadOnly();
                gridJob.fence = fences.AsReadOnly();
                for (var i = 0; i < count; i++) gridJob.Execute(i);
                var move = MoveTicks(pawn);
                job = new PathFinderJob
                {
                    indices = map.cellIndices, heuristicStrength = 0, moveTicksCardinal = move.Cardinal, moveTicksDiagonal = move.Diagonal,
                    traverseParams = query.traverseParams, calcGrid = chain.CalcGrid, frontier = chain.Frontier,
                    path = chain.Path, result = chain.Result, grid = output.grid.AsReadOnly(), providerCost = chain.Providers.AsReadOnly(),
                    blocked = chain.Blocked.AsReadOnly(), excludedRectIndices = chain.Excluded,
                };
                map.pathFinder.MapData.ParameterizePathJob(ref job);
                connectivity = new NativeArray<CellConnection>(count, Allocator.Persistent);
                for (var i = 0; i < count; i++) connectivity[i] = job.connectivity[i];
                // Reopen connections into a replaced cell (an old wall becoming a door), retaining map-edge bounds.
                foreach (var at in pieces.Keys.Concat(floors.Keys).Distinct())
                foreach (var offset in GenAdj.AdjacentCellsAndInside)
                {
                    var from = at + offset;
                    if (!from.InBounds(map)) continue;
                    var connections = CellConnection.Self;
                    foreach (var connection in CellConnection.AllNeighbours)
                        if ((from + connection.Offset()).InBounds(map)) connections |= connection;
                    connectivity[map.cellIndices.CellToIndex(from)] = connections;
                }
                job.connectivity = connectivity.AsReadOnly();
                job.buildings = buildings.AsReadOnly();
                job.fences = fences.AsReadOnly();
            }
            catch { Dispose(); throw; }
        }

        private void Add(IntVec3 at, ThingDef def, string stuff)
        {
            if (!at.InBounds(map)) throw new InvalidOperationException("Projected route furniture is outside the map.");
            pieces[at] = def;
            if (!typeof(Building_Door).IsAssignableFrom(def.thingClass)) return;
            var material = def.MadeFromStuff ? PlacementPreviewOperation.ResolveThingDef(stuff) : null;
            if (def.MadeFromStuff && material is null) throw new InvalidOperationException("Projected door stuff is unresolved.");
            var door = ThingMaker.MakeThing(def, material) as Building_Door ?? throw new InvalidOperationException("Projected door could not be constructed.");
            door.SetFaction(Faction.OfPlayer);
            doors[at] = door;
        }

        // PathGrid.CalculatedCostAt's static normal/fence-blocked cell equation (1.7), on the projected
        // terrain and things. PathGridJob applies the native pawn-specific legality and all other costs afterwards.
        private int StaticCost(IntVec3 at, ThingDef? replacement)
        {
            var terrain = floors.TryGetValue(at, out var floor) ? floor : map.terrainGrid.TerrainAt(at);
            if (terrain is null || terrain.passability == Traversability.Impassable) return PathGrid.ImpassableCost;
            var cost = terrain.pathCost;
            var defs = map.thingGrid.ThingsListAt(at).Where(t => replacement is null || t is not Verse.Building).Select(t => t.def);
            if (replacement is not null) defs = defs.Append(replacement);
            foreach (var def in defs)
            {
                if (def.passability == Traversability.Impassable || pawn.FenceBlocked && def.IsFence) return PathGrid.ImpassableCost;
                cost = Math.Max(cost, def.pathCost);
            }
            cost = Math.Max(cost, WeatherBuildupUtility.MovementTicksAddOn(map.snowGrid.GetCategory(at)));
            if (ModsConfig.OdysseyActive) cost = Math.Max(cost, WeatherBuildupUtility.MovementTicksAddOn(map.sandGrid.GetCategory(at)));
            foreach (var offset in GenAdj.AdjacentCellsAndInside)
            {
                var near = at + offset;
                if (near.InBounds(map) && map.thingGrid.ThingsListAt(near).OfType<Fire>().Any(f => f.parent is null)) cost += offset == IntVec3.Zero ? 1000 : 150;
            }
            return cost;
        }

        internal (double? There, IntVec3? Arrival) Interaction(IntVec3 from, IntVec3 prisoner)
        {
            if (!from.InBounds(map) || !prisoner.InBounds(map)) return (null, null);
            // GotoPrisoner first reaches Touch; GotoInteractablePosition then requires native sight of
            // the prisoner. TouchPathEndModeUtility permits a corner when either side is walkable and doorless.
            var reached = Search(from, prisoner, touch: true);
            if (reached is not { } arrival) return (null, null);
            var position = arrival.Cell;
            var there = arrival.Ticks;
            bool Sight(IntVec3 a) => GenSight.PointsOnLineOfSight(a, prisoner).Skip(1).SkipLast(1)
                .All(c => pieces.TryGetValue(c, out var def) ? !def.blockLight : c.CanBeSeenOver(map));
            if (!Sight(position))
            {
                var candidates = GenAdj.AdjacentCells.Select(d => prisoner + d).Where(c => c.InBounds(map) && c != position && Sight(c))
                    .Select(c => Search(position, c, touch: false)).OfType<(IntVec3 Cell, double Ticks)>().OrderBy(p => p.Cell.DistanceToSquared(position)).ToArray();
                if (candidates.Length == 0 && Search(position, prisoner, touch: false) is { } inside) candidates = [inside];
                if (candidates.Length == 0) return (null, null);
                position = candidates[0].Cell;
                there += candidates[0].Ticks;
            }
            return (there, position);
        }

        internal double? ReturnTo(IntVec3 from, IntVec3 to) => Search(from, to, touch: false)?.Ticks;

        private (IntVec3 Cell, double Ticks)? Search(IntVec3 from, IntVec3 to, bool touch)
        {
            using var request = map.pathFinder.CreateRequest(from, to, null, TraverseParms.For(pawn, Danger.None), null, PathEndMode.OnCell, pawn);
            var doorJob = new PathGridDoorsBlockedJob();
            access.ParameterizeDoors(map.pathFinder, doorJob, request, chain.Providers, chain.Blocked);
            doorJob.Execute();
            foreach (var at in pieces.Keys)
            {
                var i = map.cellIndices.CellToIndex(at);
                chain.Providers[i] = doors.TryGetValue(at, out var door)
                    ? door.PawnCanOpen(pawn) ? (ushort)door.TicksToOpenNow : ushort.MaxValue : (ushort)0;
            }
            chain.Excluded.Clear();
            if (touch)
            {
                bool Side(IntVec3 c) => c.InBounds(map) && direct[map.cellIndices.CellToIndex(c)] < PathGrid.ImpassableCost
                    && (pieces.TryGetValue(c, out var def) ? !typeof(Building_Door).IsAssignableFrom(def.thingClass) : c.GetDoor(map) is null);
                foreach (var d in GenAdj.AdjacentCells.Where(d => d.x != 0 && d.z != 0))
                {
                    var a = to + new IntVec3(d.x, 0, 0);
                    var b = to + new IntVec3(0, 0, d.z);
                    if (!Side(a) && !Side(b))
                    {
                        var corner = to + d;
                        if (corner.InBounds(map)) chain.Excluded.Add(map.cellIndices.CellToIndex(corner));
                    }
                }
            }
            job.start = from; job.destCell = to;
            job.destRect = (touch ? CellRect.SingleCell(to).ExpandedBy(1) : CellRect.SingleCell(to)).ClipInsideMap(map);
            job.Execute();
            return chain.Path.Length == 0 ? null : (chain.Path[0], chain.Result.Value.pathCost);
        }

        public void Dispose()
        {
            chain.Dispose();
            if (output.grid.IsCreated) output.Dispose();
            if (direct.IsCreated) direct.Dispose();
            if (connectivity.IsCreated) connectivity.Dispose();
            if (buildings.IsCreated) buildings.Dispose();
            if (fences.IsCreated) fences.Dispose();
        }
    }
}
