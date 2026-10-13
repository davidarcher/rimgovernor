namespace RimGovernor.Brain.Policy;

// Go: combat_drugs.go
//
// Combat drugs on draft: a drafted defender takes the combat drug it carries (go-juice, or yayo) once per
// fight, when a hostile comes within its weapon range plus the dose margin and the fight is worth the drug:
// the squad is outmatched, or a mech or a go-juiced raider is in it. A lone manhunter rat is fought sober.
// The order names the preferred carried combat drug (CombatView.Drugs); native validates it and refuses a
// child, a pawn already high, or one addicted to, in withdrawal from or highly tolerant of its chemical.

public static partial class PolicyFunctions
{
    /// <summary>The cells beyond weapon range a dose goes in, so the high (about a day) starts before contact, not on a long approach.</summary>
    internal const double DoseMargin = 10.0;

    /// <summary>
    /// Reports a fight the squad is outmatched in, or one with a live mech or go-juiced hostile. Outmatched
    /// counts only with a known eligible armed defender: with none the comparison measures missing facts, not
    /// the fight.
    /// </summary>
    private static bool WorthDosing(CombatView view, Dictionary<PawnId, CombatPawnState> state)
    {
        var known = view.Defenders.Items.Any(d => SquadDefenderEligible(d) && Positive(d.Armed));
        if (known && Outmatched(view))
        {
            return true;
        }

        foreach (var t in view.Threats.Items)
        {
            if (t.Building || Positive(t.Dead) || Positive(t.Downed))
            {
                continue;
            }

            state.TryGetValue(ThreatPawn(t), out var s);
            if (t.Mech || (s is not null && s.GoJuice && !s.Downed && !s.Dead))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// This stop's drug orders: each orderable, standing, undosed defender (not a rescuer, not mid-aim) with
    /// no other order this stop and a live hostile pawn in reach, so a dose never displaces a move or an
    /// attack; a fight sheltering or scattered doses no one. Each pawn is dosed at most once per fight,
    /// refused or not.
    /// </summary>
    private static List<CombatOrder> DoseOrders(
        CombatView view,
        CombatMemory m,
        IReadOnlyList<CombatOrder> orders,
        HashSet<PawnId> orderable,
        Dictionary<PawnId, CombatPawnState> state
    )
    {
        var doses = new List<CombatOrder>();
        if (view.Drugs.Count == 0 || m.Wait || m.PodWait || m.Scattered || m.Tactic == CombatTactic.Shelter || !WorthDosing(view, state))
        {
            return doses;
        }

        var hostiles = new List<CombatPawnState>();
        foreach (var t in view.Threats.Items)
        {
            if (state.TryGetValue(ThreatPawn(t), out var s) && !t.Building && !s.Dead && !s.Downed && !Positive(t.Dead) && !Positive(t.Downed))
            {
                hostiles.Add(s);
            }
        }

        foreach (var id in view.Orderable.Items)
        {
            if (!state.TryGetValue(id, out var s) || orders.Any(o => o.Pawn == id) || !orderable.Contains(id) || s.Dead || s.Downed || s.GoJuice || InterruptsAim(s) || m.Dosed.Contains(id) || m.Rescue?.Carrying(id) == true)
            {
                continue;
            }

            if (!hostiles.Any(h => WithinDose(s, h, m.EffectiveTuning().DoseMargin)))
            {
                continue;
            }

            if (!s.CarriedDrugs.TryGetValue(out var carried))
            {
                continue;
            }

            var drug = "";
            foreach (var candidate in view.Drugs.Items)
            {
                if (carried.Items.Contains(candidate))
                {
                    drug = candidate;
                    break;
                }
            }

            if (drug.Length == 0)
            {
                continue;
            }

            m.Dosed.Add(id);
            doses.Add(new CombatOrder { Pawn = id, Kind = CombatOrderKind.Drug, Drug = drug, Reason = CombatOrderReason.Drug });
        }

        return doses;
    }

    /// <summary>Reports h within the defender's weapon range plus the dose margin; an unknown cell counts as out of reach (a dose waits for the facts).</summary>
    private static bool WithinDose(CombatPawnState d, CombatPawnState h, double margin)
    {
        if (!d.Cell.IsKnown || !h.Cell.IsKnown)
        {
            return false;
        }

        return InRange(d.Cell, Math.Max(d.WeaponRange, 1) + margin, h);
    }
}
