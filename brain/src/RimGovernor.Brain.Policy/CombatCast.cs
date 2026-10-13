namespace RimGovernor.Brain.Policy;

// Go: combat_cast.go
//
// Psycasts in the fight: a psycaster that holds a known combat psycast casts it at the fight's hostiles or
// wounded defenders. A cast is a psycast_cast order, not a pawn job: the defense planner sends it as the
// generic Ability action, where native owns the guards (psyfocus, entropy, cooldown, range, target arm).

/// <summary>
/// What a psycast does in a fight; the royalty read carries no effect category, so the psycast families name
/// the vanilla defs by family and every other def (utility, movement, an unknown mod def) is never cast.
/// </summary>
internal enum CastFamily
{
    None,

    /// <summary>Mends a wounded defender; it outranks the rest.</summary>
    Heal,

    /// <summary>Disables a hostile.</summary>
    Stun,

    /// <summary>Damages hostiles.</summary>
    Burst,

    /// <summary>Protects the caster.</summary>
    Defensive,
}

/// <summary>
/// Records a cast this fight: the stale royalty read has not seen it, so its cooldown, psyfocus cost and
/// neural heat still count against the caster.
/// </summary>
public sealed record CastMark(PawnId Pawn, string Def, Tick Tick, double Psyfocus, double Entropy, int Cooldown);

public static partial class PolicyFunctions
{
    private static readonly Dictionary<string, CastFamily> _psycastFamilies = new(StringComparer.Ordinal)
    {
        ["Painblock"] = CastFamily.Heal,
        ["Stun"] = CastFamily.Stun,
        ["Flashstorm"] = CastFamily.Burst,
        ["Firewall"] = CastFamily.Burst,
        ["Skipshield"] = CastFamily.Defensive,
    };

    /// <summary>The most a caster stands from a target and still casts: native refuses a target out of range, so the policy sends none past it.</summary>
    private const double CastReach = 20.0;

    /// <summary>The health fraction under which a defender is mended.</summary>
    private const double CastHealBelow = 0.6;

    /// <summary>Sums the psyfocus and neural heat of a caster's casts this fight.</summary>
    private static (double Focus, double Heat) CastSpent(CombatMemory m, PawnId pawn)
    {
        double focus = 0, heat = 0;
        foreach (var c in m.Casts)
        {
            if (c.Pawn == pawn)
            {
                focus += c.Psyfocus;
                heat += c.Entropy;
            }
        }

        return (focus, heat);
    }

    private static bool CastCooling(CombatView view, CombatMemory m, PawnId pawn, string def) =>
        m.Casts.Any(c => c.Pawn == pawn && c.Def == def && view.Tick.Value - c.Tick.Value < c.Cooldown);

    /// <summary>A known psycast that is affordable and off cooldown for a caster; every unread fact holds it.</summary>
    private static bool CastReady(CombatView view, CombatMemory m, PsycasterState state, PawnId pawn, Psycast c)
    {
        var ck = c.PsyfocusCost.TryGetValue(out var cost);
        var gk = c.Entropy.TryGetValue(out var gain);
        var kk = c.CooldownTicks.IsKnown;
        var lk = c.CooldownRemaining.TryGetValue(out var left);
        var fk = state.Psyfocus.TryGetValue(out var focus);
        var hk = state.Entropy.TryGetValue(out var heat);
        var mk = state.EntropyMax.TryGetValue(out var ceiling);
        if (!ck || !gk || !kk || !lk || !fk || !hk || !mk || left != 0 || CastCooling(view, m, pawn, c.Def))
        {
            return false;
        }

        var (spentFocus, spentHeat) = CastSpent(m, pawn);
        return focus - spentFocus >= cost && heat + spentHeat + gain <= ceiling;
    }

    /// <summary>
    /// This stop's psycast casts: with a known royalty read and a live hostile, each standing colonist casts
    /// at most one ready combat psycast, the heal first, then stun, burst and defensive, in the order the read
    /// lists them. The target is the arm the ability takes (target kind), in reach of the caster; with none
    /// found the psycast waits.
    /// </summary>
    private static List<CombatOrder> CastCalls(CombatView view, CombatMemory m)
    {
        var calls = new List<CombatOrder>();
        if (!view.Royalty.TryGetValue(out var royalty) || view.Hunt || LiveHostiles(view).Count == 0)
        {
            return calls;
        }

        var down = DownPawns(view);
        foreach (var caster in royalty.Psycasts.Keys.OrderBy(k => k))
        {
            var pawn = caster;
            var (at, known) = PawnCell(view, pawn);
            if (down.Contains(pawn) || !known || !view.Defenders.Items.Any(d => d.Id == pawn))
            {
                continue;
            }

            CombatOrder best = new();
            Psycast bestCast = default!;
            var bestFamily = CastFamily.None;
            foreach (var c in royalty.Psycasts[caster].Items)
            {
                var family = _psycastFamilies.GetValueOrDefault(c.Def);
                if (family == CastFamily.None || (bestFamily != CastFamily.None && family >= bestFamily) || !CastReady(view, m, royalty.Casters[caster], pawn, c))
                {
                    continue;
                }

                var (order, found) = CastOrder(view, pawn, at, c, family);
                if (found)
                {
                    best = order;
                    bestCast = c;
                    bestFamily = family;
                }
            }

            if (bestFamily == CastFamily.None)
            {
                continue;
            }

            bestCast.PsyfocusCost.TryGetValue(out var cost);
            bestCast.Entropy.TryGetValue(out var gain);
            bestCast.CooldownTicks.TryGetValue(out var cool);
            m.Casts.Add(new CastMark(pawn, bestCast.Def, view.Tick, cost, gain, cool));
            calls.Add(best);
        }

        return calls;
    }

    /// <summary>The cast order of a psycast of a family at its target: the arm must match the ability's target kind (an unclassified kind holds).</summary>
    private static (CombatOrder Order, bool Found) CastOrder(CombatView view, PawnId caster, Cell at, Psycast c, CastFamily family)
    {
        var order = new CombatOrder { Pawn = caster, Kind = CombatOrderKind.Cast, Permit = c.Def, Reason = CombatOrderReason.Cast, Arm = c.Target };
        var hostile = CastNearestHostile(view, at);
        switch (family)
        {
            case CastFamily.Heal:
                {
                    var (wounded, cell, found) = WoundedDefender(view, at);
                    if (!found)
                    {
                        return (order, false);
                    }

                    switch (c.Target)
                    {
                        case PsycastTarget.Pawn:
                            return (order with { Target = wounded }, true);
                        case PsycastTarget.Self:
                            return (order, wounded == caster && cell == at);
                    }

                    break;
                }

            case CastFamily.Stun:
                if (c.Target == PsycastTarget.Pawn && hostile is not null)
                {
                    return (order with { Target = hostile.Id }, true);
                }

                break;
            case CastFamily.Burst:
                switch (c.Target)
                {
                    case PsycastTarget.Pawn:
                        if (hostile is not null)
                        {
                            return (order with { Target = hostile.Id }, true);
                        }

                        break;
                    case PsycastTarget.Cell:
                        if (StrikeCell(view) is { } strike && Dist(at, strike) <= CastReach)
                        {
                            return (order with { Cell = strike }, true);
                        }

                        break;
                }

                break;
            case CastFamily.Defensive:
                if (hostile is null)
                {
                    return (order, false);
                }

                switch (c.Target)
                {
                    case PsycastTarget.Self:
                        return (order, true);
                    case PsycastTarget.Pawn:
                        return (order with { Target = caster }, true);
                }

                break;
        }

        return (order, false);
    }

    private static (Cell Cell, bool Known) PawnCell(CombatView view, PawnId id)
    {
        foreach (var p in view.Pawns.Items)
        {
            if (p.Id == id)
            {
                var known = p.Cell.TryGetValue(out var c);
                return (c, known);
            }
        }

        return (default, false);
    }

    /// <summary>The live hostile pawns with a known cell, in view order.</summary>
    private static List<CombatPawnState> LiveHostiles(CombatView view)
    {
        var hostile = new Dictionary<PawnId, bool>();
        foreach (var t in view.Threats.Items)
        {
            hostile[ThreatPawn(t)] = !t.Building && !Positive(t.Dead) && !Positive(t.Downed);
        }

        return [.. view.Pawns.Items.Where(p => p.Cell.IsKnown && hostile.GetValueOrDefault(p.Id) && !p.Dead && !p.Downed)];
    }

    /// <summary>The live hostile nearest from within CastReach.</summary>
    private static CombatPawnState? CastNearestHostile(CombatView view, Cell from)
    {
        CombatPawnState? best = null;
        var nearest = CastReach;
        foreach (var p in LiveHostiles(view))
        {
            p.Cell.TryGetValue(out var c);
            var d = Dist(from, c);
            if (d <= nearest && (best is null || d < nearest))
            {
                best = p;
                nearest = d;
            }
        }

        return best;
    }

    /// <summary>The defender with the lowest known health fraction under CastHealBelow within CastReach of from.</summary>
    private static (PawnId Id, Cell Cell, bool Found) WoundedDefender(CombatView view, Cell from)
    {
        PawnId id = default;
        Cell cell = default;
        var lowest = CastHealBelow;
        var found = false;
        foreach (var d in view.Defenders.Items)
        {
            var ok = d.HealthFraction.TryGetValue(out var health);
            var (c, cellKnown) = PawnCell(view, d.Id);
            if (!ok || !cellKnown || health >= lowest || Dist(from, c) > CastReach)
            {
                continue;
            }

            id = d.Id;
            cell = c;
            lowest = health;
            found = true;
        }

        return (id, cell, found);
    }
}
