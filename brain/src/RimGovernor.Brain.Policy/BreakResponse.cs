namespace RimGovernor.Brain.Policy;


public sealed record BreakResponder
{
    public required SquadDefenderFacts SquadDefenderFacts { get; init; }

    public Fact<Cell> Cell { get; init; }

    public PawnId ID => SquadDefenderFacts.Id;
}

public static class BreakResponseRules
{
    public const int BreakExclusionRadius = 8;

    /// <summary>
    /// Uses the native aggression fact, never an inferred mood risk or a closed list that silently misses
    /// modded mental states. Social fights resolve through native ticks without a subdue response; treating
    /// them as combat would hold the clock that must resolve them.
    /// </summary>
    public static bool AggressiveBreak(EmergencyPawn p)
    {
        var state = p.MentalState;
        return state is not null && state.IsAggro && state.DefName != "SocialFighting" && !p.Dead && !p.Downed;
    }

    /// <summary>
    /// Chooses at most two healthy responders. Distance is primary; stable pawn identity breaks ties.
    /// SUBDUE selects a legal blunt verb, fists included, as the game's own subdue does: a responder is either
    /// armed with a melee weapon or known unarmed. A ranged-equipped responder is never one (shooting a colonist
    /// is not a subdue). A squad of fists alone is refused against a breaker wielding a melee weapon, or one not among
    /// the candidates: a squad there leads with the nearest melee-armed responder.
    /// </summary>
    public static List<PawnId> SelectBreakSquad(EmergencyPawn target, Fact<Cell> cell, IEnumerable<BreakResponder> candidates)
    {
        if (!cell.TryGetValue(out var at) || !AggressiveBreak(target))
        {
            return [];
        }

        static bool MeleeArmed(SquadDefenderFacts f) =>
            f.Armed && f.MeleeEquipped;

        var pool = new List<BreakResponder>();
        var breakerUnarmedOrRanged = false;
        foreach (var c in candidates)
        {
            var f = c.SquadDefenderFacts;
            if (c.ID == target.Id)
            {
                breakerUnarmedOrRanged = !f.MeleeEquipped;
                continue;
            }
            if (SquadDefenseRules.SquadDefenderEligible(f) && !f.RangedEquipped && (MeleeArmed(f) || !f.Armed) && c.Cell.IsKnown)
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

        var sorted = pool.OrderBy(Distance).ThenBy(c => c.ID.Value, StringComparer.Ordinal).ToList();
        var squad = sorted.Take(2).ToList();
        if (breakerUnarmedOrRanged || squad.Exists(c => MeleeArmed(c.SquadDefenderFacts)))
        {
            return [.. squad.Select(c => c.ID)];
        }
        var lead = sorted.Find(c => MeleeArmed(c.SquadDefenderFacts));
        return lead is null ? [] : [lead.ID, squad[0].ID];
    }

    public static bool InBreakRadius(Cell cell, Cell target)
    {
        long dx = (long)cell.X - target.X;
        long dz = (long)cell.Z - target.Z;
        return (dx * dx) + (dz * dz) <= BreakExclusionRadius * BreakExclusionRadius;
    }
}
