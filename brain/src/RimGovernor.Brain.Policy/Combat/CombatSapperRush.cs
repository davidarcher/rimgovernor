using RimGovernor.Contracts.Wire.Clock;
namespace RimGovernor.Brain.Policy;

public static partial class CombatSapperRules
{
    /// <summary>How close to the breach, in cells, a hostile sets the posted brawlers on it.</summary>
    internal const int RushRange = 4;

    /// <summary>
    /// The sapper tactic's melee rush. The brawlers posted just inside the breach hold with no target, so none
    /// walks out to fight in the open, until a live hostile comes within the rush range of the breach cell or a
    /// breach stop arrives. Then every posted brawler leaves its cell and attacks the hostile nearest the
    /// breach, and keeps rushing until the next formation.
    /// </summary>
    internal static CombatMemory SapperRush(CombatView view, StopEvent stop, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Sapper || m.SapperGuard is not BreachGuard.Guarding { Wall: var breach })
        {
            return m with { Rushing = false };
        }
        PawnId nearest = default;
        var near = -1L;
        foreach (var h in CombatThreatRules.RankThreats(view))
        {
            var c = h.Cell;
            if ((near < 0 || CombatManhunterRules.Distance2(c, breach) < near))
            {
                nearest = h.Id;
                near = CombatManhunterRules.Distance2(c, breach);
            }
        }
        if (nearest.IsEmpty)
        {
            return m;
        }
        long rush = m.EffectiveTuning().RushRange;
        if (near <= rush * rush || stop.Kind == CombatEvent.Breach)
        {
            m = m with { Rushing = true };
        }
        if (!m.Rushing)
        {
            return m;
        }
        var roles = m.Roles.Select(r => r.Duty == CombatDuty.Blocker && !r.Ranged ? r with { Post = new CombatPost.Free(), Target = nearest } : r).ToList();
        return m with { Roles = EquatableArray.From(roles) };
    }
}
