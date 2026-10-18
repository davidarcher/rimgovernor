#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
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
/// finds searches directly from then on (the live game's batched requests all resolved empty, #3556). The legs are
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
        store = Store.For(map);
        store.Settle();
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
    /// the finder's own map data is (PathFinderMapData's map events): a region, room or fog change and a game condition
    /// drop every leg (passability, danger); a building, a path-relevant thing, a terrain or path-cost change, a
    /// reservation or a haul en route drops the legs crossing its cells. A leg with no path has no cells and lasts until
    /// the next full drop. Door open states are not watched: a leg through a door was priced at the door's state when
    /// searched, as every leg of one read is. Changes are noted as they happen and applied at the next read
    /// (<see cref="Settle"/>).
    /// </summary>
    private sealed class Store
    {
        private static readonly ConditionalWeakTable<Map, Store> Stores = new();

        internal static Store For(Map map) => Stores.GetValue(map, m => new Store(m));

        private readonly Map map;
        private readonly Dictionary<Key, Leg?> legs = new();
        private readonly List<Key>?[] crossing;
        private readonly HashSet<int> dirtyCells = new();
        private readonly List<CellRect> dirtyRects = new();
        private bool dropAll;

        private Store(Map map)
        {
            this.map = map;
            crossing = new List<Key>?[map.cellIndices.NumGridCells];
            var events = map.events;
            events.RegionsRoomsChanged += DropAll;
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
            legs[key] = leg;
            if (leg != null) foreach (var cell in leg.Cells) (crossing[cell] ??= new()).Add(key);
            return leg;
        }

        /// <summary>Applies the changes noted since the last read: every leg, or the legs crossing the changed cells.</summary>
        internal void Settle()
        {
            if (dropAll)
            {
                legs.Clear();
                Array.Clear(crossing, 0, crossing.Length);
                dropAll = false;
                dirtyCells.Clear();
                dirtyRects.Clear();
                return;
            }
            foreach (var rect in dirtyRects)
                foreach (var cell in rect.Cells)
                    if (cell.InBounds(map)) dirtyCells.Add(map.cellIndices.CellToIndex(cell));
            dirtyRects.Clear();
            foreach (var index in dirtyCells)
            {
                var keys = crossing[index];
                if (keys == null) continue;
                foreach (var key in keys) legs.Remove(key);
                crossing[index] = null;
            }
            dirtyCells.Clear();
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
