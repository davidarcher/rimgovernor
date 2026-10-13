namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The frame a sortie's gunners shoot: the job target of a live besieger on a FinishFrame job, the least
    /// id; empty with no builder. The first mortar frame ends the sortie, so these are the sandbags.
    /// </summary>
    internal static PawnId SiegeFrame(CombatView view)
    {
        var besiegers = LiveBesiegers(view);
        PawnId frame = default;
        foreach (var p in view.Pawns)
        {
            if (besiegers.ContainsKey(p.Id) && p.Job == "FinishFrame" && !p.Target.IsEmpty && (frame.IsEmpty || p.Target < frame))
            {
                frame = p.Target;
            }
        }
        return frame;
    }

    /// <summary>
    /// Points a sortie's gunners at the frame the besiegers are building, so the mortars never get finished;
    /// brawlers keep their besieger. The attack order takes a hostile building.
    /// </summary>
    internal static CombatMemory SiegeSnipe(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Siege || m.SiegeMode != SiegeMode.Sortie)
        {
            return m;
        }
        var frame = SiegeFrame(view);
        if (frame.IsEmpty)
        {
            return m;
        }
        return m with { Roles = EquatableArray.From(m.Roles.Select(r => r.Ranged ? r with { Target = frame } : r)) };
    }
}
