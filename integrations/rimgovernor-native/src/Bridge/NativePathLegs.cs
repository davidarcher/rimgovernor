#nullable enable
using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>
/// One native read's paths, each searched once (#3341). PathFinder's cost is the path's summed cell costs plus each
/// step's move ticks, cardinal or diagonal (PathFinderJob). Free colonists share the cell grid, so a leg searched for one
/// colonist is every colonist's: a pawn's cost re-prices the leg's cardinal and diagonal steps at its own move ticks. A
/// leg is keyed by its origin cell, target and end mode, so a colonist's legs from its own cell are its own.
/// </summary>
internal sealed class NativePathLegs
{
    private readonly Map map;
    private readonly Dictionary<(IntVec3 From, IntVec3 To, Thing? Thing, PathEndMode Mode), Leg?> legs = new();

    internal NativePathLegs(Map map) => this.map = map;

    /// <summary>The searches run so far: one per distinct leg.</summary>
    internal int Searches => legs.Count;

    private sealed record Leg(float Cost, int Cardinal, int Diagonal, int CardinalTicks, int DiagonalTicks);

    /// <summary>The pawn's ticks over the leg, or null when no path exists.</summary>
    internal float? Cost(Pawn pawn, IntVec3 from, LocalTargetInfo to, PathEndMode mode)
    {
        var key = (from, to.Cell, to.Thing, mode);
        if (!legs.TryGetValue(key, out var leg))
        {
            using var path = map.pathFinder.FindPathNow(from, to, TraverseParms.For(pawn, Danger.None), peMode: mode);
            legs[key] = leg = path.Found ? Measure(path, pawn) : null;
        }
        if (leg == null) return null;
        var (cardinal, diagonal) = MoveTicks(pawn);
        return leg.Cost + leg.Cardinal * (cardinal - leg.CardinalTicks) + leg.Diagonal * (diagonal - leg.DiagonalTicks);
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
