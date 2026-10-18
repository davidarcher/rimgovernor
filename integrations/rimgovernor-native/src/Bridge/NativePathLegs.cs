#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// workers instead of one at a time on the game thread (#3556).
/// </summary>
internal sealed class NativePathLegs
{
    /// <summary>
    /// Requests pushed to the path finder per scheduling round. Each scheduled search holds a grid-sized state and
    /// output the finder pools for good, so the round is bounded; the game's workers split a round between them.
    /// </summary>
    internal const int BatchRound = 64;

    private readonly Map map;
    private readonly Dictionary<(IntVec3 From, IntVec3 To, Thing? Thing, PathEndMode Mode), Leg?> legs = new();
    private readonly List<(Pawn Pawn, IntVec3 From, LocalTargetInfo To, PathEndMode Mode)> wanted = new();
    private readonly HashSet<(IntVec3 From, IntVec3 To, Thing? Thing, PathEndMode Mode)> wantedKeys = new();
    private bool collecting;

    internal NativePathLegs(Map map) => this.map = map;

    /// <summary>The searches run so far: one per distinct leg.</summary>
    internal int Searches => legs.Count;

    /// <summary>The stopwatch ticks the searches took so far, batched rounds included.</summary>
    internal long SearchTicks { get; private set; }

    private sealed record Leg(float Cost, int Cardinal, int Diagonal, int CardinalTicks, int DiagonalTicks);

    /// <summary>
    /// The pawn's ticks over the leg, or null when no path exists. While a <see cref="Batched{T}"/> read collects, a
    /// leg not yet searched is noted and answered as 0 without a search.
    /// </summary>
    internal float? Cost(Pawn pawn, IntVec3 from, LocalTargetInfo to, PathEndMode mode)
    {
        var key = (from, to.Cell, to.Thing, mode);
        if (!legs.TryGetValue(key, out var leg))
        {
            if (collecting)
            {
                if (wantedKeys.Add(key)) wanted.Add((pawn, from, to, mode));
                return 0f;
            }
            var started = Stopwatch.GetTimestamp();
            using var path = map.pathFinder.FindPathNow(from, to, TraverseParms.For(pawn, Danger.None), peMode: mode);
            legs[key] = leg = path.Found ? Measure(path, pawn) : null;
            SearchTicks += Stopwatch.GetTimestamp() - started;
        }
        if (leg == null) return null;
        var (cardinal, diagonal) = MoveTicks(pawn);
        return leg.Cost + leg.Cardinal * (cardinal - leg.CardinalTicks) + leg.Diagonal * (diagonal - leg.DiagonalTicks);
    }

    /// <summary>
    /// Runs <paramref name="read"/> twice: once collecting every leg it asks (each unsearched <see cref="Cost"/>
    /// answers 0 without a search, so the read asks every leg it could need and its result is discarded), then, after
    /// one batched search of the collected legs, for real against the searched legs. A leg the second run asks that
    /// the first did not is searched on its own. The rows are those of a read that searched each leg as it asked it:
    /// the finder runs the same job per request either way.
    /// </summary>
    internal T Batched<T>(Func<T> read)
    {
        collecting = true;
        try { read(); }
        finally { collecting = false; }
        Search();
        return read();
    }

    // Pushes the wanted legs to the path finder in rounds of BatchRound and ticks it twice per round: the first tick
    // schedules the pushed requests across the job workers (PathFinder.ScheduleBatchedPathJobs), the second completes
    // them and resolves each request with its path.
    private void Search()
    {
        if (wanted.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        var finder = map.pathFinder;
        var requests = new List<((IntVec3, IntVec3, Thing?, PathEndMode) Key, PathRequest Request, Pawn Pawn)>(BatchRound);
        for (var first = 0; first < wanted.Count; first += BatchRound)
        {
            requests.Clear();
            for (var i = first; i < wanted.Count && i < first + BatchRound; i++)
            {
                var (pawn, from, to, mode) = wanted[i];
                var request = finder.CreateRequest(from, to, null, TraverseParms.For(pawn, Danger.None), null, mode, pawn);
                finder.PushRequest(request);
                requests.Add(((from, to.Cell, to.Thing, mode), request, pawn));
            }
            finder.PathFinderTick();
            finder.PathFinderTick();
            foreach (var (key, request, pawn) in requests)
            {
                legs[key] = request.TryGetPath(out var path) && path != null && path.Found ? Measure(path, pawn) : null;
                request.Dispose();
            }
        }
        wanted.Clear();
        wantedKeys.Clear();
        SearchTicks += Stopwatch.GetTimestamp() - started;
    }

    private static Leg Measure(PawnPath path, Pawn pawn)
    {
        var nodes = path.NodesReversed;
        int cardinal = 0, diagonal = 0;
        for (var i = 1; i < nodes.Count; i++)
        {
            if (nodes[i].x != nodes[i - 1].x && nodes[i].z != nodes[i - 1].z) diagonal++;
            else cardinal++;
        }
        var (cardinalTicks, diagonalTicks) = MoveTicks(pawn);
        return new(path.TotalCost, cardinal, diagonal, cardinalTicks, diagonalTicks);
    }

    // PathFinder.ParameterizePathJob: Mathf.RoundToInt of the pawn's move ticks.
    private static (int Cardinal, int Diagonal) MoveTicks(Pawn pawn) =>
        ((int)Math.Round(pawn.TicksPerMoveCardinal), (int)Math.Round(pawn.TicksPerMoveDiagonal));
}
