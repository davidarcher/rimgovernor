using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>One colonist able to use a worn lance now: alive, standing, out of a mental state and capable of violence (the verb is violent).</summary>
public sealed record LanceUser(PawnId Pawn, string Item);

public static partial class PopulationRules
{
    /// <summary>
    /// The worn items whose target verb downs a pawn with a hediff rather than violent damage: the psychic
    /// shock lance adds PsychicShock, so the target skips the death-on-downed roll a gun or blade would risk.
    /// The insanity lance drives a pawn berserk instead, so it is not a capture tool.
    /// </summary>
    public static readonly IReadOnlyDictionary<Resource, bool> LanceDefs = new Dictionary<Resource, bool>
    {
        [new Resource("Apparel_PsychicShockLance")] = true,
    };

    /// <summary>Ranks a standing target by its skills: the sum of its non-disabled skill levels.</summary>
    internal static int LanceScore(PrisonerProspect p)
    {
        var total = 0;
        foreach (var s in p.Skills)
        {
            if (!s.Disabled)
            {
                total += s.Level;
            }
        }
        return total;
    }

    /// <summary>
    /// The standing hostile humanlike a lance should down while the colony is below
    /// FindingConstants.PopulationTarget: known recruitable, not downed, not a prisoner, and the best skills;
    /// ties go to the lowest pawn ID. Null when the colony is at target, its size is unknown or no row
    /// qualifies. Who wears a lance is the planner's combat read (SelectLanceUse).
    /// </summary>
    public static PawnId? LanceCandidate(RoundsFacts f)
    {
        var census = f.Population;
        if (census.Colony.Colonists >= PopulationTargetFor(f.ColonistGoal))
        {
            return null;
        }
        PawnId? best = null;
        var bestScore = -1;
        foreach (var row in census.Custody)
        {
            if (row.Dead || row.Downed || !row.Hostile || row.Prisoner || !row.Recruitable)
            {
                continue;
            }
            var score = LanceScore(row.Prospect);
            if (score > bestScore || (score == bestScore && row.Pawn.CompareTo(best!.Value) < 0))
            {
                (best, bestScore) = (row.Pawn, score);
            }
        }
        return best;
    }

    /// <summary>Reports whether any observed colonist wears a lance.</summary>
    internal static bool LanceWorn(GearObservation gear)
    {
        foreach (var p in gear.Pawns)
        {
            if (p.Apparel.Any(a => PopulationRules.LanceDefs.GetValueOrDefault(a.Definition)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports whether an able lance wearer other than target can down it; false when target is absent or nobody
    /// can use one now. Native checks the verb's charges, the target and the colonist again when the intent applies.
    /// </summary>
    public static bool LanceUsable(PawnId? target, IReadOnlyList<LanceUser> users) =>
        target is { } t && t.Value.Length != 0 && users.Any(u => u.Pawn != t && u.Item.Length != 0);
}
