namespace RimGovernor.Brain.Policy;

// Go: break_response.go

public sealed record BreakResponder
{
    public required SquadDefenderFacts SquadDefenderFacts { get; init; }

    public Fact<Cell> Cell { get; init; }

    public PawnId ID => SquadDefenderFacts.Id;
}

public static partial class PolicyFunctions
{
    public const int BreakExclusionRadius = 8;

    /// <summary>
    /// Uses the native aggression fact, never an inferred mood risk or a closed list that silently misses
    /// modded mental states. Social fights resolve through native ticks without a subdue response; treating
    /// them as combat would hold the clock that must resolve them.
    /// </summary>
    public static bool AggressiveBreak(EmergencyPawn p)
    {
        var known = p.MentalState.TryGetValue(out var state);
        var dk = p.Dead.TryGetValue(out var dead);
        var wk = p.Downed.TryGetValue(out var down);
        return known && state!.IsAggro && state.DefName != "SocialFighting" && dk && wk && !dead && !down;
    }

    /// <summary>
    /// Chooses at most two healthy melee responders. Distance is primary; stable pawn identity breaks ties.
    /// SUBDUE selects a legal blunt verb.
    /// </summary>
    public static List<PawnId> SelectBreakSquad(EmergencyPawn target, Fact<Cell> cell, IEnumerable<BreakResponder> candidates)
    {
        if (!cell.TryGetValue(out var at) || !AggressiveBreak(target))
        {
            return [];
        }

        var pool = new List<BreakResponder>();
        foreach (var c in candidates)
        {
            var f = c.SquadDefenderFacts;
            var rk = f.RangedEquipped.TryGetValue(out var ranged);
            var mk = f.MeleeEquipped.TryGetValue(out var melee);
            var ak = f.Armed.TryGetValue(out var armed);
            var ck = c.Cell.IsKnown;
            if (c.ID != target.Id && SquadDefenderEligible(f) && mk && melee && rk && !ranged && ak && armed && ck)
            {
                pool.Add(c);
            }
        }

        long Distance(BreakResponder c)
        {
            c.Cell.TryGetValue(out var p);
            long dx = (long)p.X - at.X;
            long dz = (long)p.Z - at.Z;
            return (dx * dx) + (dz * dz);
        }

        var sorted = pool.OrderBy(Distance).ThenBy(c => c.ID.Value, StringComparer.Ordinal);
        return [.. sorted.Take(2).Select(c => c.ID)];
    }

    public static bool InBreakRadius(Cell cell, Cell target)
    {
        long dx = (long)cell.X - target.X;
        long dz = (long)cell.Z - target.Z;
        return (dx * dx) + (dz * dz) <= BreakExclusionRadius * BreakExclusionRadius;
    }
}
