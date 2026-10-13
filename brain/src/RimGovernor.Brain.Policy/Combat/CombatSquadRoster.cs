namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The native combat-health threshold: a defender at or below it is not selected for a squad and a role
    /// holder at it falls back.
    /// </summary>
    internal const double SquadHurtHealth = (float)0.5005;

    /// <summary>The squad's role holders that are alive, at or below SquadHurtHealth and not yet pulled back.</summary>
    internal static List<PawnId> SquadHurt(CombatView view, IReadOnlyList<CombatRole> roles)
    {
        var live = view.Live();
        var result = new List<PawnId>();
        foreach (var r in roles)
        {
            if (r.Retreat || !live.GetValueOrDefault(r.Pawn))
            {
                continue;
            }
            foreach (var d in view.Defenders)
            {
                if (d.HealthFraction.TryGetValue(out var health) && d.Id == r.Pawn && health <= SquadHurtHealth)
                {
                    result.Add(r.Pawn);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Reports a squad fight that lost a fighter: a defender downed at this stop, or a role holder hurt past
    /// the selector's gate and not yet pulled back. A re-formation draws the replacements.
    /// </summary>
    internal static bool SquadRosterChanged(CombatView view, StopEvent stop, CombatMemory m)
    {
        if (stop.Kind == CombatStopKind.Downed && IsDefender(view, stop.Pawn))
        {
            return true;
        }
        return SquadHurt(view, m.Roles).Count > 0;
    }

    /// <summary>
    /// Keeps a squad formation's hurt former holders out of the fight: each moves away from the hostiles as the
    /// shelter tactic's colonists do, on a Retreat role the next re-formation carries over while the pawn stays
    /// out of the new formation. <paramref name="prev"/> are the roles before the re-formation;
    /// <paramref name="formed"/> the new ones.
    /// </summary>
    internal static List<CombatRole> SquadFallBack(CombatView view, GeometryReply geometry, IReadOnlyList<Cell> unreachable, IReadOnlyList<CombatRole> prev, IReadOnlyList<CombatRole> formed)
    {
        var live = view.Live();
        var hurt = new HashSet<PawnId>(SquadHurt(view, prev));
        foreach (var r in prev)
        {
            if (r.Retreat && live.GetValueOrDefault(r.Pawn))
            {
                hurt.Add(r.Pawn);
            }
        }
        foreach (var r in formed)
        {
            hurt.Remove(r.Pawn);
        }
        if (hurt.Count == 0)
        {
            return formed.ToList();
        }
        var result = formed.ToList();
        result.AddRange(ShelterRolesFor(view, geometry, unreachable, d => hurt.Contains(d.Id)));
        return SortRoles(result);
    }
}
