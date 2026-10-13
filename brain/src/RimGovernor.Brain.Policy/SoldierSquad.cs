namespace RimGovernor.Brain.Policy;

/// <summary>
/// The bot's persistent soldier squad: the colony's best fighters, about a third of the colonists.
/// Membership is sticky; the store keeps it in the save. GearSoldier (drafted only) is separate.
/// </summary>
public sealed record SoldierSquad
{
    /// <summary>Sorted.</summary>
    public IReadOnlyList<PawnId> Members { get; init; } = [];

    /// <summary>Whether pawn is a squad member. Members is sorted.</summary>
    public bool IsSoldier(PawnId pawn)
    {
        var low = 0;
        var high = Members.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var cmp = Members[mid].CompareTo(pawn);
            if (cmp == 0)
            {
                return true;
            }
            if (cmp < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return false;
    }

    /// <summary>ceil(colonists/3), at least 1.</summary>
    public static int SoldierSquadSize(int colonists) => Math.Max(1, (colonists + 2) / 3);

    /// <summary>
    /// Ranks a fighter: the better of Shooting and Melee, the passion of that skill breaking a tie. Ok is false
    /// when the pawn cannot be a soldier (violence-incapable, a child) or a deciding fact is unknown; Known is
    /// false only for the unknown case.
    /// </summary>
    private static (int Score, bool Ok, bool Known) SoldierScore(WorkPawn p)
    {
        var ak = p.Age.TryGetValue(out var age);
        var ik = p.Incapable.TryGetValue(out var incapable);
        var sk = p.Skills.TryGetValue(out var skills);
        if (!ak || !ik || !sk)
        {
            return (0, false, false);
        }
        if (age < ChildAge)
        {
            return (0, false, true);
        }
        foreach (var w in incapable!)
        {
            if (w == WorkType.Violent)
            {
                return (0, false, true);
            }
        }
        var score = -1;
        foreach (var s in skills!)
        {
            if (s.Disabled || (s.Name != "Shooting" && s.Name != "Melee"))
            {
                continue;
            }
            score = Math.Max(score, (s.Level * 3) + GearPassionRank(s.Passion));
        }
        return (score, score >= 0, true);
    }

    private readonly record struct Fighter(PawnId Id, int Score, bool Member);

    /// <summary>
    /// Returns the squad for the current colonists. Members still present and fit stay; the gap to
    /// SoldierSquadSize(len(pawns)) is filled with the best non-members, and a shrunken target drops the
    /// weakest members. Known is false when a pawn's deciding facts are unknown; the caller then keeps the
    /// previous squad.
    /// </summary>
    public static (SoldierSquad Squad, bool Known) ReviewSoldierSquad(SoldierSquad previous, IReadOnlyList<WorkPawn> pawns)
    {
        var members = new List<Fighter>();
        var others = new List<Fighter>();
        foreach (var p in pawns)
        {
            var (score, ok, known) = SoldierScore(p);
            if (!known)
            {
                return (previous, false);
            }
            if (!ok)
            {
                continue;
            }
            var f = new Fighter(p.Id, score, previous.IsSoldier(p.Id));
            if (f.Member)
            {
                members.Add(f);
            }
            else
            {
                others.Add(f);
            }
        }
        static List<Fighter> Better(List<Fighter> list) => [.. list.OrderByDescending(f => f.Score).ThenBy(f => f.Id)];
        members = Better(members);
        others = Better(others);
        var size = SoldierSquadSize(pawns.Count);
        if (members.Count > size)
        {
            members = members.GetRange(0, size);
        }
        foreach (var f in others)
        {
            if (members.Count >= size)
            {
                break;
            }
            members.Add(f);
        }
        return (new SoldierSquad { Members = [.. members.Select(f => f.Id).OrderBy(id => id)] }, true);
    }
}
