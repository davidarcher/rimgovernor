using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>One colonist able to use a worn lance now: alive, standing, out of a mental state and capable of violence (the verb is violent).</summary>
public sealed record LanceUser(PawnId Pawn, string Item);

/// <summary>One lance use: the colonist, the worn lance and the hostile it downs.</summary>
public sealed record LanceChoice(PawnId User, PawnId Target, string Item);

public static partial class PolicyConstants
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
}

public static partial class PolicyFunctions
{
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

    /// <summary>LanceCandidate while some colonist wears a lance (f.Gear): the review's MaintainPopulation deficit. Null when none.</summary>
    public static PawnId? LanceTarget(RoundsFacts f) => LanceWorn(f.Gear) ? LanceCandidate(f) : null;

    /// <summary>
    /// The standing hostile humanlike a lance should down while the colony is below
    /// DomainConstants.PopulationTarget: known recruitable, not downed, not a prisoner, and the best skills;
    /// ties go to the lowest pawn ID. Null when the colony is at target, its size is unknown or no row
    /// qualifies. Who wears a lance is the planner's combat read (SelectLanceUse).
    /// </summary>
    public static PawnId? LanceCandidate(RoundsFacts f)
    {
        if (!f.PrisonerColony.TryGetValue(out var colony) || colony.Colonists >= DomainConstants.PopulationTarget)
        {
            return null;
        }
        if (!f.Custody.TryGetValue(out var rows))
        {
            return null;
        }
        PawnId? best = null;
        var bestScore = -1;
        foreach (var row in rows)
        {
            var dk = row.Dead.TryGetValue(out var dead);
            var wk = row.Downed.TryGetValue(out var downed);
            var hk = row.Hostile.TryGetValue(out var hostile);
            var pk = row.Prisoner.TryGetValue(out var prisoner);
            var rk = row.Recruitable.TryGetValue(out var recruitable);
            var sk = row.Prospect.TryGetValue(out var prospect);
            if (!dk || !wk || !hk || !pk || !rk || !sk || dead || downed || !hostile || prisoner || !recruitable)
            {
                continue;
            }
            var score = LanceScore(prospect!);
            if (score > bestScore || (score == bestScore && row.Pawn.CompareTo(best!.Value) < 0))
            {
                (best, bestScore) = (row.Pawn, score);
            }
        }
        return best;
    }

    /// <summary>Reports whether any observed colonist wears a lance.</summary>
    internal static bool LanceWorn(Fact<GearObservation> gear)
    {
        if (!gear.TryGetValue(out var g))
        {
            return false;
        }
        foreach (var p in g.Pawns)
        {
            if (!p.Apparel.TryGetValue(out var apparel))
            {
                continue;
            }
            if (apparel.Any(a => PolicyConstants.LanceDefs.GetValueOrDefault(a.Definition)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pairs target with the lowest-ID able lance wearer; null when target is absent or nobody can use one
    /// now. Native checks the verb's charges, the target and the colonist again when the intent applies.
    /// </summary>
    public static LanceChoice? SelectLanceUse(PawnId? target, IReadOnlyList<LanceUser> users)
    {
        if (target is not { } t || t.Value.Length == 0 || users.Count == 0)
        {
            return null;
        }
        foreach (var u in users.OrderBy(u => u.Pawn))
        {
            if (u.Pawn != t && u.Item.Length != 0)
            {
                return new LanceChoice(u.Pawn, t, u.Item);
            }
        }
        return null;
    }
}
