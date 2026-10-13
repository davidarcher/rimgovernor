namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// MaintainIdeoRoles keeps the ideoligion's role precepts filled: each active role with a free place is
    /// given to the believer who best fits it, through the shared Assign intent (the role precept's id is
    /// the assignable thing). Which skills a role asks for, how many pawns it takes and when it is active
    /// are the catalog's role defs (IdeologyDefs.Roles); no role or skill name is listed here. A pawn that
    /// already holds a role is never moved.
    /// </summary>
    public static readonly ConcernId MaintainIdeoRoles = new("MaintainIdeoRoles");
}

/// <summary>One pawn taking one role precept. Role is the role precept's id (the thing Assign targets); Def its def.</summary>
public sealed record RoleAssignment(PawnId Pawn, string Role, string Def);

public static partial class PolicyFunctions
{
    /// <summary>
    /// Scores pawn for def: the sum, over the def's skill requirements, of the best level among the skills
    /// of that requirement. Ok is false when the pawn fails a skill requirement (no listed skill is enabled
    /// and at the minimum). Requirements without skills are the game's to judge when the assignment applies.
    /// </summary>
    internal static (int Score, bool Ok) RoleFit(RoleDef def, IReadOnlyList<WorkSkill> skills)
    {
        var score = 0;
        foreach (var requirement in def.Requirements)
        {
            if (requirement.Skills.Count == 0)
            {
                continue;
            }
            int best = 0;
            var met = false;
            foreach (var need in requirement.Skills)
            {
                foreach (var s in skills)
                {
                    if (s.Name == need.Skill && !s.Disabled && s.Level >= need.MinLevel)
                    {
                        met = true;
                        best = Math.Max(best, s.Level);
                    }
                }
            }
            if (!met)
            {
                return (0, false);
            }
            score += best;
        }
        return (score, true);
    }

    /// <summary>
    /// The assignments the ideoligion owes now: for each active role with fewer holders than its def's
    /// MaxCount, the best-fitting available believer who holds no role, by fit, then certainty, then id.
    /// The pawn row must show the ideoligion's id and no role; its skills must be read. Unknown without the
    /// ideoligion or the pawn rows; a role whose def is missing from the catalog fails the decode, so none
    /// reaches here. At most one pawn is chosen per role and per call, so a role with several places fills
    /// over successive calls as the section shows each holder. Sorted by role id.
    /// </summary>
    public static Fact<IReadOnlyList<RoleAssignment>> RoleAssignments(Fact<Ideoligion> ideology, Fact<IReadOnlyList<WorkPawn>> pawns)
    {
        if (!ideology.TryGetValue(out var ideo) || !pawns.TryGetValue(out var rows))
        {
            return Unknown<IReadOnlyList<RoleAssignment>>();
        }
        var pool = new List<(PawnId Id, IReadOnlyList<WorkSkill> Skills, double Certainty)>();
        foreach (var row in rows)
        {
            if (row.Available.TryGetValue(out var available) && available
                && row.PolicyInputs.TryGetValue(out var inputs)
                && row.Skills.TryGetValue(out var skills)
                && inputs.Ideo == ideo.Facts.IdeoId && inputs.IdeoRole == string.Empty)
            {
                inputs.IdeoCertainty.TryGetValue(out var certainty);
                pool.Add((row.Id, skills, certainty));
            }
        }
        var roles = ideo.Facts.Roles.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var output = new List<RoleAssignment>();
        var taken = new HashSet<PawnId>();
        foreach (var role in roles)
        {
            if (!ideo.Defs.Roles.TryGetValue(role.Def, out var def) || !role.Active || role.Pawns.Count >= def.MaxCount)
            {
                continue;
            }
            (PawnId Id, IReadOnlyList<WorkSkill> Skills, double Certainty) best = default;
            var bestScore = 0;
            var found = false;
            foreach (var c in pool)
            {
                if (taken.Contains(c.Id))
                {
                    continue;
                }
                var (score, fits) = RoleFit(def, c.Skills);
                if (!fits)
                {
                    continue;
                }
                if (!found || score > bestScore || (score == bestScore && (c.Certainty > best.Certainty || (c.Certainty == best.Certainty && c.Id < best.Id))))
                {
                    best = c;
                    bestScore = score;
                    found = true;
                }
            }
            if (found)
            {
                taken.Add(best.Id);
                output.Add(new RoleAssignment(best.Id, role.Id, role.Def));
            }
        }
        return Known<IReadOnlyList<RoleAssignment>>(output);
    }

    /// <summary>
    /// Measures MaintainIdeoRoles: known false when no role has a fitting believer waiting, unknown while
    /// the ideoligion or pawns are.
    /// </summary>
    public static Fact<bool> RolesOwed(Fact<Ideoligion> ideology, Fact<IReadOnlyList<WorkPawn>> pawns)
    {
        if (!RoleAssignments(ideology, pawns).TryGetValue(out var owed))
        {
            return Unknown<bool>();
        }
        return Known(owed.Count > 0);
    }
}
