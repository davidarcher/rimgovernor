#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Gilzoide.ManagedJobs;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Unity.Collections;
using Unity.Jobs;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>
/// One native read's paths, each searched once (#3341). PathFinder's cost is the path's summed cell costs plus each
/// step's move ticks, cardinal or diagonal (PathFinderJob). Free colonists share the cell grid, so a leg searched for one
/// colonist is every colonist's: a pawn's cost re-prices the leg's cardinal and diagonal steps at its own move ticks. A
/// leg is keyed by its origin cell, target and end mode, so a colonist's legs from its own cell are its own.
/// A read that asks many legs runs <see cref="Batched{T}"/>, which searches them together on the game's path job
/// workers instead of one at a time on the game thread (#3556). A batched request that resolves with no path is not
/// believed: the leg is searched again on its own, and a map whose batched search misses a path the direct search
/// finds searches directly from then on (the live game's batched requests all resolved empty, #3556). A read that asks
/// every leg from many origins to many cells fills them first by <see cref="Floods"/>, one Dijkstra flood per origin
/// on the job workers instead of one search per leg (the coupled forecast's route matrix, #3685). The legs are
/// kept across reads by the map's <see cref="Store"/> until the map changes under them, so a read finds the legs an
/// earlier read searched.
/// </summary>
internal sealed class NativePathLegs
{
    /// <summary>
    /// The fewest requests pushed to the path finder per scheduling round. A round's paths are emitted together when
    /// the finder resolves them (PathFinderTick), and the map's PawnPathPool reports a leak once more than twice the
    /// spawned pawns plus four paths exist at once, so a round is the spawned pawns plus two, at least this many.
    /// </summary>
    internal const int MinRound = 4;

    private readonly Map map;
    private readonly Store store;
    private readonly List<(Pawn Pawn, IntVec3 From, LocalTargetInfo To, PathEndMode Mode)> wanted = new();
    private readonly HashSet<Key> wantedKeys = new();
    private bool collecting;

    internal NativePathLegs(Map map)
    {
        this.map = map;
        Gathered.Refresh(map.pathFinder.MapData);
        store = Store.For(map);
        store.Settle();
    }

    /// <summary>
    /// The finder's map data gathers its cell changes once per game tick (PathFinderMapData.GatherData), so a map
    /// changed after this tick's gather is searched on the data from before the change until the clock moves: a paused
    /// or held tick-0 read that follows a staged or player change finds no path from a cell the change touched (#3537),
    /// and the store would keep that no-path leg. A read whose map data holds changes not yet gathered on this tick has
    /// them gathered at its first search: the gather tick is set back one tick (to -1 at tick 0, a full recompute).
    /// When a game update renames the members it is skipped, once warned.
    /// </summary>
    private static class Gathered
    {
        private static readonly FieldInfo? Tick = AccessTools.Field(typeof(PathFinderMapData), "lastGatherTick");
        private static readonly FieldInfo? Cells = AccessTools.Field(typeof(PathFinderMapData), "cellDeltas");
        private static readonly FieldInfo? Rects = AccessTools.Field(typeof(PathFinderMapData), "cellRectDeltas");
        private static readonly bool Found = Tick?.FieldType == typeof(int) && Cells?.FieldType == typeof(List<IntVec3>)
            && Rects?.FieldType == typeof(List<CellRect>) || Warn();

        private static bool Warn()
        {
            Log.Warning("[RimGovernor] The path finder map data's gather members were not found; a read on the tick of a map change may search the data from before it (#3537).");
            return false;
        }

        internal static void Refresh(PathFinderMapData data)
        {
            if (!Found || Tick!.GetValue(data) is not int tick || tick != GenTicks.TicksGame) return;
            if (((List<IntVec3>)Cells!.GetValue(data)!).Count == 0 && ((List<CellRect>)Rects!.GetValue(data)!).Count == 0) return;
            Tick.SetValue(data, GenTicks.TicksGame - 1);
        }
    }

    /// <summary>The searches this read ran: one per leg it asked that the store did not hold.</summary>
    internal int Searches { get; private set; }

    /// <summary>The stopwatch ticks the searches took so far, batched rounds included.</summary>
    internal long SearchTicks { get; private set; }

    private readonly record struct Key(IntVec3 From, IntVec3 To, Thing? Thing, PathEndMode Mode);

    /// <summary>A searched leg: its cost and steps at the move ticks it was searched with, and the cells it crosses.</summary>
    private sealed record Leg(float Cost, int Cardinal, int Diagonal, int CardinalTicks, int DiagonalTicks, int[] Cells);

    /// <summary>
    /// A found leg: its cost and steps at the move ticks it was searched with, priced for any pawn's move ticks by
    /// <see cref="For"/>. A read that prices one leg for every colonist finds it once (<see cref="Find"/>) and prices
    /// it per pawn (#3556).
    /// </summary>
    internal readonly record struct Found(float Cost, int Cardinal, int Diagonal, int CardinalTicks, int DiagonalTicks)
    {
        /// <summary>The ticks over the leg at the move ticks given (<see cref="MoveTicks"/>).</summary>
        internal float For((int Cardinal, int Diagonal) moveTicks) =>
            Cost + Cardinal * (moveTicks.Cardinal - CardinalTicks) + Diagonal * (moveTicks.Diagonal - DiagonalTicks);
    }

    /// <summary>
    /// The pawn's ticks over the leg, or null when no path exists. While a <see cref="Batched{T}"/> read collects, a
    /// leg not yet searched is noted and answered as 0 without a search.
    /// </summary>
    internal float? Cost(Pawn pawn, IntVec3 from, LocalTargetInfo to, PathEndMode mode) =>
        Find(pawn, from, to, mode) is { } leg ? leg.For(MoveTicks(pawn)) : null;

    /// <summary>
    /// The leg, searched for <paramref name="pawn"/> when the store lacks it, or null when no path exists. While a
    /// <see cref="Batched{T}"/> read collects, a leg not yet searched is noted and answered as a zero leg.
    /// </summary>
    internal Found? Find(Pawn pawn, IntVec3 from, LocalTargetInfo to, PathEndMode mode)
    {
        var key = new Key(from, to.Cell, to.Thing, mode);
        if (!store.TryGet(key, out var leg))
        {
            if (collecting)
            {
                if (wantedKeys.Add(key)) wanted.Add((pawn, from, to, mode));
                return default(Found);
            }
            GameThreadTrace.Mark("leg search " + from + " -> " + to + ", " + mode + ", for " + pawn);
            var started = Stopwatch.GetTimestamp();
            using var path = map.pathFinder.FindPathNow(from, to, TraverseParms.For(pawn, Danger.None), peMode: mode);
            leg = store.Put(key, path.Found ? Measure(map, path, pawn) : null);
            Searches++;
            SearchTicks += Stopwatch.GetTimestamp() - started;
        }
        if (leg == null) return null;
        return new Found(leg.Cost, leg.Cardinal, leg.Diagonal, leg.CardinalTicks, leg.DiagonalTicks);
    }

    /// <summary>
    /// Runs <paramref name="read"/> twice: once collecting every leg it asks (each unsearched <see cref="Cost"/>
    /// answers 0 without a search, so the read asks every leg it could need and its result is discarded), then, after
    /// one batched search of the collected legs, for real against the searched legs. A leg the second run asks that
    /// the first did not is searched on its own. The rows are those of a read that searched each leg as it asked it:
    /// the finder runs the same job per request either way. Inside a collecting run, a nested batched read runs
    /// once, collecting for the outer read, which searches and runs it again.
    /// </summary>
    internal T Batched<T>(Func<T> read)
    {
        if (collecting) return read();
        collecting = true;
        GameThreadTrace.Mark("batched read: collecting run");
        try { read(); }
        finally { collecting = false; }
        Search();
        GameThreadTrace.Mark("batched read: second run");
        return read();
    }

    // Pushes the wanted legs to the path finder in rounds and ticks it twice per round: the first tick schedules the
    // pushed requests across the job workers (PathFinder.ScheduleBatchedPathJobs), the second completes them and
    // resolves each request with its path. A request that resolved with no path, or did not resolve, is not stored as
    // "no path": its leg is searched directly (FindPathNow, the game thread) and that answer is stored. A map whose
    // batched round missed a path the direct search found is searched directly from then on, with one warning in the
    // game's log naming the first such leg (#3556).
    private void Search()
    {
        if (wanted.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        var finder = map.pathFinder;
        var direct = new List<int>(wanted.Count);
        if (store.Direct)
        {
            for (var i = 0; i < wanted.Count; i++) direct.Add(i);
        }
        else
        {
            var round = Math.Max(MinRound, map.mapPawns.AllPawnsSpawnedCount + 2);
            var requests = new List<(Key Key, PathRequest Request, Pawn Pawn)>(round);
            for (var first = 0; first < wanted.Count; first += round)
            {
                GameThreadTrace.Mark("legs batched search: round from leg " + first + " of " + wanted.Count + ", round " + round);
                requests.Clear();
                for (var i = first; i < wanted.Count && i < first + round; i++)
                {
                    var (pawn, from, to, mode) = wanted[i];
                    var request = finder.CreateRequest(from, to, null, TraverseParms.For(pawn, Danger.None), null, mode, pawn);
                    finder.PushRequest(request);
                    requests.Add((new Key(from, to.Cell, to.Thing, mode), request, pawn));
                }
                finder.PathFinderTick();
                finder.PathFinderTick();
                for (var j = 0; j < requests.Count; j++)
                {
                    var (key, request, pawn) = requests[j];
                    if (request.TryGetPath(out var path) && path != null && path.Found) store.Put(key, Measure(map, path, pawn));
                    else direct.Add(first + j);
                    request.Dispose();
                }
            }
        }
        var missed = 0;
        string? firstMissed = null;
        foreach (var i in direct)
        {
            var (pawn, from, to, mode) = wanted[i];
            GameThreadTrace.Mark("legs direct search " + from + " -> " + to + ", " + mode + ", for " + pawn + " (" + direct.Count + " of " + wanted.Count + " unresolved)");
            using var path = finder.FindPathNow(from, to, TraverseParms.For(pawn, Danger.None), peMode: mode);
            store.Put(new Key(from, to.Cell, to.Thing, mode), path.Found ? Measure(map, path, pawn) : null);
            if (!path.Found || store.Direct) continue;
            missed++;
            firstMissed ??= $"{from} -> {to}, {mode}, for {pawn}";
        }
        if (missed > 0)
        {
            store.Direct = true;
            Log.Warning($"[RimGovernor] The batched path search resolved {missed} of {direct.Count} unresolved legs with no path that a direct search found (first: {firstMissed}); this map's legs are searched directly from now on (#3556).");
        }
        Searches += wanted.Count;
        wanted.Clear();
        wantedKeys.Clear();
        SearchTicks += Stopwatch.GetTimestamp() - started;
    }

    /// <summary>The floods this read ran (<see cref="Floods"/>): one per origin with a wanted leg the store lacked.</summary>
    internal int FloodCount { get; private set; }

    /// <summary>The stopwatch ticks the floods took so far; also counted in <see cref="SearchTicks"/>.</summary>
    internal long FloodTicks { get; private set; }

    /// <summary>
    /// Fills the store with every origin's OnCell legs to its targets by one Dijkstra flood per origin (#3685): the
    /// game's own <see cref="PathFinderJob"/> with heuristic strength 0 and a destination off the map, so it closes every
    /// cell it can reach and its calc grid holds the cheapest cost and parent from the origin to each. The inputs are
    /// those <see cref="PathFinder.FindPathNow(IntVec3, LocalTargetInfo, TraverseParms, PathFinderCostTuning?, PathEndMode, PathRequest.IPathGridCustomizer)"/>
    /// builds for the origin's pawn (TraverseParms at <see cref="Danger.None"/>): the grid job's cost grid for the
    /// request, its doors, providers and blocking pawns (start side only, there being no destination), and the map
    /// data's connectivity, fences and buildings. All floods run on the job workers, one chain of calc grid and
    /// frontier per worker reused across its floods; the game thread waits for them. A leg's cost is the target's
    /// gCost, which can be a few ticks under the A* search's (its heuristic overestimates a diagonal step); its steps
    /// and cells come from walking the parents back to the origin, and an unreached target is a no-path leg. An origin
    /// whose wanted legs the store holds is not flooded. When the pathfinder's private members cannot be reached the
    /// floods are skipped, once warned, and the legs are left to the caller's search.
    /// </summary>
    internal void Floods(IReadOnlyList<(Pawn Pawn, IntVec3 From, IReadOnlyList<IntVec3> To)> origins)
    {
        var floods = new List<(Pawn Pawn, IntVec3 From, int[] To)>();
        var seen = new HashSet<int>();
        foreach (var (pawn, from, to) in origins)
        {
            seen.Clear();
            var missing = new List<int>();
            foreach (var cell in to)
            {
                if (!cell.InBounds(map)) continue;
                var index = map.cellIndices.CellToIndex(cell);
                if (seen.Add(index) && !store.TryGet(new Key(from, cell, null, PathEndMode.OnCell), out _)) missing.Add(index);
            }
            if (missing.Count > 0 && from.InBounds(map)) floods.Add((pawn, from, missing.ToArray()));
        }
        if (floods.Count == 0 || FinderAccess.Instance is not { } access) return;
        var started = Stopwatch.GetTimestamp();
        var finder = map.pathFinder;
        var cells = map.cellIndices.NumGridCells;
        var grids = new Dictionary<PathFinder.MapGridRequest, NativeArray<int>.ReadOnly>();
        var chains = new FloodChain[Math.Max(1, Math.Min(UnityData.MaxJobWorkerCount, floods.Count))];
        var results = new FloodLegs[floods.Count];
        var ends = new NativeArray<JobHandle>(chains.Length, Allocator.Persistent);
        try
        {
            for (var c = 0; c < chains.Length; c++) chains[c] = new FloodChain(cells);
            GameThreadTrace.Mark("legs floods: scheduling " + floods.Count + " floods on " + chains.Length + " workers");
            for (var f = 0; f < floods.Count; f++)
            {
                var (pawn, from, to) = floods[f];
                var parms = TraverseParms.For(pawn, Danger.None);
                // A destination off the map: the job never finalizes a path and expands every reachable cell.
                var request = finder.CreateRequest(from, IntVec3.Invalid, null, parms, null, PathEndMode.OnCell, pawn);
                var gridRequest = PathFinder.MapGridRequest.For(request);
                if (!grids.TryGetValue(gridRequest, out var grid)) grids[gridRequest] = grid = access.Grid(finder, request);
                ref var chain = ref chains[f % chains.Length];
                var doors = new PathGridDoorsBlockedJob();
                access.ParameterizeDoors(finder, doors, request, chain.Providers, chain.Blocked);
                var (cardinalTicks, diagonalTicks) = MoveTicks(pawn);
                var job = new PathFinderJob
                {
                    indices = map.cellIndices,
                    heuristicStrength = 0f,
                    moveTicksCardinal = cardinalTicks,
                    moveTicksDiagonal = diagonalTicks,
                    start = from,
                    destCell = IntVec3.Invalid,
                    destRect = CellRect.SingleCell(IntVec3.Invalid),
                    traverseParams = gridRequest.traverseParams,
                    calcGrid = chain.CalcGrid,
                    frontier = chain.Frontier,
                    path = chain.Path,
                    result = chain.Result,
                    grid = grid,
                    providerCost = chain.Providers.AsReadOnly(),
                    blocked = chain.Blocked.AsReadOnly(),
                    excludedRectIndices = chain.Excluded,
                };
                finder.MapData.ParameterizePathJob(ref job);
                results[f] = new FloodLegs(map.cellIndices, chain.CalcGrid, to, cardinalTicks, diagonalTicks);
                chain.End = new ManagedJob(results[f]).Schedule(job.Schedule(new ManagedJob(doors).Schedule(chain.End)));
            }
            for (var c = 0; c < chains.Length; c++) ends[c] = chains[c].End;
            JobHandle.CombineDependencies(ends).Complete();
            GameThreadTrace.Mark("legs floods: " + floods.Count + " floods complete");
        }
        finally
        {
            // Every chain completes before its buffers go: a throw while scheduling leaves the scheduled jobs running.
            foreach (var chain in chains) chain.End.Complete();
            foreach (var chain in chains) chain.Dispose();
            ends.Dispose();
        }
        var legs = 0;
        for (var f = 0; f < floods.Count; f++)
        {
            var (_, from, to) = floods[f];
            for (var t = 0; t < to.Length; t++) store.Put(new Key(from, map.cellIndices.IndexToCell(to[t]), null, PathEndMode.OnCell), results[f].Legs[t]);
            legs += to.Length;
        }
        var elapsed = Stopwatch.GetTimestamp() - started;
        FloodCount += floods.Count;
        FloodTicks += elapsed;
        Searches += legs;
        SearchTicks += elapsed;
    }

    /// <summary>
    /// One job worker's flood buffers, reused by each flood of its chain in turn (PathFinder.JobBatchState and
    /// PathUniqueState): the calc grid, the frontier, the doors job's provider and blocked grids, and the job's unused
    /// path, result and excluded-cell outputs. The frontier holds every enqueue not yet dequeued; a cell is enqueued
    /// only when a closing neighbour improves it, so at most eight times, and the frontier is sized for that.
    /// </summary>
    private struct FloodChain : IDisposable
    {
        internal NativeArray<PathFinderJob.CalcNode> CalcGrid;
        internal NativePriorityQueue<int, float, FloatMinComparer> Frontier;
        internal NativeArray<ushort> Providers;
        internal NativeArray<bool> Blocked;
        internal NativeList<IntVec3> Path;
        internal NativeReference<PathFinderJob.ResultData> Result;
        internal NativeList<int> Excluded;
        internal JobHandle End;

        internal FloodChain(int cells)
        {
            CalcGrid = new NativeArray<PathFinderJob.CalcNode>(cells, Allocator.Persistent);
            Frontier = new NativePriorityQueue<int, float, FloatMinComparer>(cells * 8 + 1, default, Allocator.Persistent);
            Providers = new NativeArray<ushort>(cells, Allocator.Persistent);
            Blocked = new NativeArray<bool>(cells, Allocator.Persistent);
            Path = new NativeList<IntVec3>(1, Allocator.Persistent);
            Result = new NativeReference<PathFinderJob.ResultData>(default, Allocator.Persistent);
            Excluded = new NativeList<int>(1, Allocator.Persistent);
            End = default;
        }

        public void Dispose()
        {
            if (CalcGrid.IsCreated) CalcGrid.Dispose();
            if (Frontier.IsCreated) Frontier.Dispose();
            if (Providers.IsCreated) Providers.Dispose();
            if (Blocked.IsCreated) Blocked.Dispose();
            if (Path.IsCreated) Path.Dispose();
            if (Result.IsCreated) Result.Dispose();
            if (Excluded.IsCreated) Excluded.Dispose();
        }
    }

    /// <summary>
    /// Reads one flood's legs from its calc grid on the job worker, before the chain's next flood clears it: per target,
    /// the closed cell's gCost, and its cardinal and diagonal steps and crossed cells walking the parents to the origin
    /// (the origin is its own parent). A target the flood never closed has no path.
    /// </summary>
    private sealed class FloodLegs(CellIndices indices, NativeArray<PathFinderJob.CalcNode> calcGrid, int[] targets, int cardinalTicks, int diagonalTicks) : IJob
    {
        internal readonly Leg?[] Legs = new Leg?[targets.Length];

        public void Execute()
        {
            var cells = new List<int>();
            for (var t = 0; t < targets.Length; t++)
            {
                var node = calcGrid[targets[t]];
                if (node.status != PathFinderJob.CalcNode.Status.Closed) continue;
                cells.Clear();
                int cardinal = 0, diagonal = 0;
                var at = targets[t];
                cells.Add(at);
                // A parent chain is at most the map's cells long; the bound only guards a corrupt grid.
                for (var guard = 0; guard < indices.NumGridCells; guard++)
                {
                    var parent = calcGrid[at].parentIndex;
                    if (parent == at) break;
                    var a = indices.IndexToCell(at);
                    var b = indices.IndexToCell(parent);
                    if (a.x != b.x && a.z != b.z) diagonal++;
                    else cardinal++;
                    cells.Add(parent);
                    at = parent;
                }
                Legs[t] = new Leg(node.gCost, cardinal, diagonal, cardinalTicks, diagonalTicks, cells.ToArray());
            }
        }
    }

    /// <summary>
    /// The pathfinder's private members a flood needs, found once (#3685): its grid-job lookup and scheduling with the
    /// completion and recycling around them (PathFinder.FindPathNow's sequence), and its doors-job parameterization.
    /// They are reached by name, there being no public route; when a game update renames any of them, the floods are
    /// skipped with one warning and the legs are searched as before.
    /// </summary>
    private sealed class FinderAccess
    {
        internal static readonly FinderAccess? Instance = Find();

        private readonly MethodInfo forceComplete, recycleGrids, scheduleGrid, parameterizeDoors;
        private readonly FieldInfo gridLookup, gridHandle, gridOutput, stateProviders, stateBlocked;
        private readonly Type stateType;

        private FinderAccess(MethodInfo forceComplete, MethodInfo recycleGrids, MethodInfo scheduleGrid, MethodInfo parameterizeDoors,
            FieldInfo gridLookup, FieldInfo gridHandle, FieldInfo gridOutput, Type stateType, FieldInfo stateProviders, FieldInfo stateBlocked)
        {
            this.forceComplete = forceComplete; this.recycleGrids = recycleGrids; this.scheduleGrid = scheduleGrid;
            this.parameterizeDoors = parameterizeDoors; this.gridLookup = gridLookup; this.gridHandle = gridHandle;
            this.gridOutput = gridOutput; this.stateType = stateType; this.stateProviders = stateProviders; this.stateBlocked = stateBlocked;
        }

        private static FinderAccess? Find()
        {
            var finder = typeof(PathFinder);
            var gridJob = AccessTools.Inner(finder, "ScheduledGridJob");
            var state = AccessTools.Inner(finder, "PathUniqueState");
            var access = gridJob == null || state == null ? null : Make(
                AccessTools.Method(finder, "ForceCompleteScheduledJobs", Type.EmptyTypes),
                AccessTools.Method(finder, "RecycleGridJobData", Type.EmptyTypes),
                AccessTools.Method(finder, "ScheduleGridJob", new[] { typeof(PathRequest), typeof(JobHandle) }),
                AccessTools.Method(finder, "ParameterizeDoorBlockedJob", new[] { typeof(PathGridDoorsBlockedJob), typeof(PathRequest), state.MakeByRefType() }),
                AccessTools.Field(finder, "gridJobLookup"), AccessTools.Field(gridJob, "handle"), AccessTools.Field(gridJob, "output"),
                state, AccessTools.Field(state, "providers"), AccessTools.Field(state, "blocked"));
            if (access == null) Log.Warning("[RimGovernor] The path finder's private grid and doors members were not found; the coupled forecast's route matrix is searched pair by pair, not flooded (#3683).");
            return access;
        }

        private static FinderAccess? Make(MethodInfo? forceComplete, MethodInfo? recycleGrids, MethodInfo? scheduleGrid, MethodInfo? parameterizeDoors,
            FieldInfo? gridLookup, FieldInfo? gridHandle, FieldInfo? gridOutput, Type state, FieldInfo? stateProviders, FieldInfo? stateBlocked) =>
            forceComplete != null && recycleGrids != null && scheduleGrid != null && parameterizeDoors != null && gridLookup != null
                && typeof(IDictionary).IsAssignableFrom(gridLookup.FieldType)
                && gridHandle?.FieldType == typeof(JobHandle) && gridOutput?.FieldType == typeof(PathFinder.GridJobOutput)
                && stateProviders?.FieldType == typeof(NativeArray<ushort>) && stateBlocked?.FieldType == typeof(NativeArray<bool>)
                ? new(forceComplete, recycleGrids, scheduleGrid, parameterizeDoors, gridLookup, gridHandle, gridOutput, state, stateProviders, stateBlocked)
                : null;

        /// <summary>The request's completed cost grid, as FindPathNow gets it: the finder's grid for the request's MapGridRequest, scheduled when missing.</summary>
        internal NativeArray<int>.ReadOnly Grid(PathFinder finder, PathRequest request)
        {
            forceComplete.Invoke(finder, null);
            if (finder.MapData.GatherData(new[] { request })) recycleGrids.Invoke(finder, null);
            var key = PathFinder.MapGridRequest.For(request);
            var lookup = (IDictionary)gridLookup.GetValue(finder)!;
            var scheduled = lookup.Contains(key) ? lookup[key]! : scheduleGrid.Invoke(finder, new object[] { request, default(JobHandle) })!;
            ((JobHandle)gridHandle.GetValue(scheduled)!).Complete();
            return ((PathFinder.GridJobOutput)gridOutput.GetValue(scheduled)!).grid.AsReadOnly();
        }

        /// <summary>The finder's own doors-job parameterization, writing the job's providers and blocked cells into the arrays given.</summary>
        internal void ParameterizeDoors(PathFinder finder, PathGridDoorsBlockedJob job, PathRequest request, NativeArray<ushort> providers, NativeArray<bool> blocked)
        {
            var state = Activator.CreateInstance(stateType)!;
            stateProviders.SetValue(state, providers);
            stateBlocked.SetValue(state, blocked);
            parameterizeDoors.Invoke(finder, new[] { job, request, state });
        }
    }

    private static Leg Measure(Map map, PawnPath path, Pawn pawn)
    {
        var nodes = path.NodesReversed;
        int cardinal = 0, diagonal = 0;
        var cells = new int[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            cells[i] = map.cellIndices.CellToIndex(nodes[i]);
            if (i == 0) continue;
            if (nodes[i].x != nodes[i - 1].x && nodes[i].z != nodes[i - 1].z) diagonal++;
            else cardinal++;
        }
        var (cardinalTicks, diagonalTicks) = MoveTicks(pawn);
        return new(path.TotalCost, cardinal, diagonal, cardinalTicks, diagonalTicks, cells);
    }

    /// <summary>The pawn's cardinal and diagonal move ticks (PathFinder.ParameterizePathJob: Mathf.RoundToInt of each).</summary>
    internal static (int Cardinal, int Diagonal) MoveTicks(Pawn pawn) =>
        ((int)Math.Round(pawn.TicksPerMoveCardinal), (int)Math.Round(pawn.TicksPerMoveDiagonal));

    /// <summary>
    /// The map's legs kept across reads, with the cells each crosses, dropped when the map changes under them the way
    /// the finder's own map data is (PathFinderMapData's map events), each change dropping only the legs it could alter
    /// (#3686). A fog change and a game condition drop every leg (passability, danger). A region or room change drops the
    /// no-path legs, which it can connect. A building, a path-relevant thing, a terrain or path-cost change, a
    /// reservation or a haul en route dirties its cells, and a dirty cell drops the no-path legs, the legs crossing it
    /// (an obstacle or cost added on the path), and the legs a path through it could beat (an obstacle removed or a cell
    /// made cheaper): those whose octile lower bound from the origin through the cell to the end, priced at the leg's own
    /// move ticks with no cell cost, is under the leg's cost. More than <see cref="MaxDirtyCells"/> dirty cells in one
    /// settle drop every leg instead. Door open states are not watched: a leg through a door was priced at the door's
    /// state when searched, as every leg of one read is. Changes are noted as they happen and applied at the next read
    /// (<see cref="Settle"/>).
    /// </summary>
    private sealed class Store
    {
        /// <summary>The most dirty cells one settle tests each leg against; more drop every leg.</summary>
        internal const int MaxDirtyCells = 256;

        private static readonly ConditionalWeakTable<Map, Store> Stores = new();

        internal static Store For(Map map) => Stores.GetValue(map, m => new Store(m));

        private readonly Map map;
        private readonly Dictionary<Key, Leg?> legs = new();
        // Per cell, the legs stored crossing it. An entry whose key now holds another leg (or none) is stale and skipped;
        // the index is rebuilt from the live legs once stale entries outnumber them.
        private readonly List<(Key Key, Leg Leg)>?[] crossing;
        private long indexed, live;
        private readonly HashSet<int> dirtyCells = new();
        private readonly List<CellRect> dirtyRects = new();
        private bool dropAll, dropNoPath;

        private Store(Map map)
        {
            this.map = map;
            crossing = new List<(Key, Leg)>?[map.cellIndices.NumGridCells];
            var events = map.events;
            events.RegionsRoomsChanged += () => dropNoPath = true;
            events.MapFogged += DropAll;
            events.CellFogChanged += (_, _) => DropAll();
            events.GameConditionAdded += _ => DropAll();
            events.GameConditionRemoved += _ => DropAll();
            events.BuildingSpawned += b => Dirty(b.OccupiedRect());
            events.BuildingDespawned += b => Dirty(b.OccupiedRect());
            events.ThingSpawned += ThingChanged;
            events.ThingDespawned += ThingChanged;
            events.TerrainChanged += Dirty;
            events.PathCostRecalculate += Dirty;
            events.ReservationAdded += ReservationChanged;
            events.ReservationRemoved += ReservationChanged;
            events.HaulEnrouteAdded += (thing, _, _, _) => Dirty(thing.OccupiedRect());
            events.HaulEnrouteReleased += (thing, _) => Dirty(thing.OccupiedRect());
        }

        /// <summary>
        /// The map's legs are searched directly, one synchronous search each, because a batched round resolved a leg
        /// with no path that the direct search found (<see cref="Search"/>). Set for the map's life.
        /// </summary>
        internal bool Direct { get; set; }

        internal bool TryGet(Key key, out Leg? leg) => legs.TryGetValue(key, out leg);

        internal Leg? Put(Key key, Leg? leg)
        {
            if (legs.TryGetValue(key, out var old) && old != null) live -= old.Cells.Length;
            legs[key] = leg;
            if (leg == null) return leg;
            foreach (var cell in leg.Cells) (crossing[cell] ??= new()).Add((key, leg));
            indexed += leg.Cells.Length;
            live += leg.Cells.Length;
            return leg;
        }

        /// <summary>
        /// Applies the changes noted since the last read: every leg, or the no-path legs and, per dirty cell, the legs
        /// crossing it and the legs a path through it could beat.
        /// </summary>
        internal void Settle()
        {
            var area = dirtyCells.Count;
            foreach (var rect in dirtyRects) area += rect.Area;
            if (dropAll || area > MaxDirtyCells)
            {
                Clear();
                return;
            }
            foreach (var rect in dirtyRects)
                foreach (var cell in rect.Cells)
                    if (cell.InBounds(map)) dirtyCells.Add(map.cellIndices.CellToIndex(cell));
            dirtyRects.Clear();
            if (!dropNoPath && dirtyCells.Count == 0) return;
            dropNoPath = false;
            var drop = new List<Key>();
            foreach (var pair in legs)
                if (pair.Value == null) drop.Add(pair.Key);
            foreach (var key in drop) legs.Remove(key);
            if (dirtyCells.Count == 0) return;
            foreach (var index in dirtyCells)
            {
                var entries = crossing[index];
                if (entries == null) continue;
                foreach (var (key, leg) in entries)
                    if (legs.TryGetValue(key, out var held) && ReferenceEquals(held, leg)) Remove(key, leg);
                indexed -= entries.Count;
                crossing[index] = null;
            }
            var dirty = new IntVec3[dirtyCells.Count];
            var d = 0;
            foreach (var index in dirtyCells) dirty[d++] = map.cellIndices.IndexToCell(index);
            dirtyCells.Clear();
            var beaten = new List<(Key, Leg)>();
            foreach (var pair in legs)
                if (pair.Value is { } leg && Beatable(pair.Key, leg, dirty)) beaten.Add((pair.Key, leg));
            foreach (var (key, leg) in beaten) Remove(key, leg);
            if (indexed > 2 * live + 4096) Reindex();
        }

        // Whether a path through any of the cells could cost less than the leg: from the origin to the cell and on to the
        // leg's end, each octile distance priced at the leg's move ticks and no cell cost, a cost no such path goes under.
        // The end is the leg's last cell (its destination, or a thing's interaction cell), except a touch end, which any
        // cell touching the target can be.
        private bool Beatable(Key key, Leg leg, IntVec3[] cells)
        {
            var end = key.Mode is PathEndMode.Touch or PathEndMode.ClosestTouch
                ? (key.Thing is { } thing ? GenAdj.OccupiedRect(key.To, thing.Rotation, thing.def.size) : CellRect.SingleCell(key.To)).ExpandedBy(1)
                : CellRect.SingleCell(map.cellIndices.IndexToCell(leg.Cells[0]));
            var cardinal = leg.CardinalTicks;
            var diagonal = Math.Min(leg.DiagonalTicks, 2 * cardinal);
            foreach (var cell in cells)
            {
                var bound = Octile(Math.Abs(cell.x - key.From.x), Math.Abs(cell.z - key.From.z), cardinal, diagonal)
                    + Octile(Gap(cell.x, end.minX, end.maxX), Gap(cell.z, end.minZ, end.maxZ), cardinal, diagonal);
                if (bound < leg.Cost) return true;
            }
            return false;
        }

        private static int Gap(int at, int min, int max) => at < min ? min - at : at > max ? at - max : 0;

        private static int Octile(int dx, int dz, int cardinal, int diagonal) =>
            dx < dz ? diagonal * dx + cardinal * (dz - dx) : diagonal * dz + cardinal * (dx - dz);

        private void Remove(Key key, Leg leg)
        {
            legs.Remove(key);
            live -= leg.Cells.Length;
        }

        private void Reindex()
        {
            Array.Clear(crossing, 0, crossing.Length);
            indexed = 0;
            foreach (var pair in legs)
            {
                if (pair.Value is not { } leg) continue;
                foreach (var cell in leg.Cells) (crossing[cell] ??= new()).Add((pair.Key, leg));
                indexed += leg.Cells.Length;
            }
        }

        private void Clear()
        {
            legs.Clear();
            Array.Clear(crossing, 0, crossing.Length);
            indexed = live = 0;
            dropAll = dropNoPath = false;
            dirtyCells.Clear();
            dirtyRects.Clear();
        }

        private void DropAll() => dropAll = true;

        private void Dirty(IntVec3 cell) { if (cell.InBounds(map)) dirtyCells.Add(map.cellIndices.CellToIndex(cell)); }

        private void Dirty(CellRect rect) => dirtyRects.Add(rect);

        // PathFinderMapData.Notify_ThingSpawnedDespawned: the things the finder prices, not a pawn's attachments.
        private void ThingChanged(Thing thing)
        {
            if (!thing.def.pathfinderDangerous || thing is AttachableThing { parent: Pawn }) return;
            if (thing.def.size.Area == 1) Dirty(thing.Position);
            else Dirty(thing.OccupiedRect());
        }

        private void ReservationChanged(ReservationManager.Reservation reservation)
        {
            if (reservation.Target.HasThing) Dirty(reservation.Target.Thing.OccupiedRect());
        }
    }
}
