namespace RimGovernor.Brain.Policy;

// Go: combat_kite.go

public static partial class PolicyFunctions
{
    // Kiting constants.

    /// <summary>
    /// How much faster than the fastest chaser a kiter must be (the wiki's 120% Moving); <see cref="KiteFastRatio"/>
    /// against a pack with an animal at least a colonist's speed (the wiki's 140%).
    /// </summary>
    internal const double KiteSpeedRatio = 1.2;

    internal const double KiteFastRatio = 1.4;

    /// <summary>
    /// The weapon range, in cells, of a kiter's long-range gun: an assault rifle (30.9) or longer. A revolver
    /// or SMG must close in and gets caught.
    /// </summary>
    internal const double KiteMinRange = 30.0;

    /// <summary>The heaviest worn sharp armor rating a kiter wears: light armor, below a marine's. Unknown armor does not refuse.</summary>
    internal const double KiteMaxArmor = 0.8;

    /// <summary>A hostile this close to the kiter, in cells, starts the lead even before it targets the kiter.</summary>
    internal const int KiteLeadRange = 8;

    /// <summary>
    /// The one kiting step: a manhunter pack, and an unsupported mech raid under the hold or the sapper
    /// tactic. When KiteLead finds the chasers kitable, the fastest eligible gunner (KiterEligible) is the
    /// kiter. It baits the nearest hostile from where it stands; once a hostile targets it or comes within the
    /// lead range, it leads: it retreats to the lead cell, so the chaser crosses the firing squad's line (and
    /// the flank turrets), and engages again from there.
    /// </summary>
    private static void Kite(CombatView view, CombatMemory m)
    {
        if (KiteLead(view, m) is not { } lead)
        {
            m.Kiter = default;
            m.Leading = false;
            return;
        }

        var ranked = RankThreats(view);
        if (ranked.Count == 0)
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var armor = new Dictionary<PawnId, Fact<double>>();
        foreach (var d in view.Defenders.Items)
        {
            armor[d.Id] = d.Armor;
        }

        var i = -1;
        for (var j = 0; j < m.Roles.Count; j++)
        {
            if (m.Roles[j].Pawn == m.Kiter)
            {
                i = j;
            }
        }

        if (i < 0)
        {
            m.Kiter = default;
            m.Leading = false;
            var best = 0.0;
            for (var j = 0; j < m.Roles.Count; j++)
            {
                var r = m.Roles[j];
                var s = state.GetValueOrDefault(r.Pawn) ?? new CombatPawnState();
                if (r.Duty == CombatDuty.None && KiterEligible(r, s, armor.GetValueOrDefault(r.Pawn), lead.Need) && s.MoveSpeed > best)
                {
                    i = j;
                    best = s.MoveSpeed;
                }
            }

            if (i < 0)
            {
                return;
            }

            m.Kiter = m.Roles[i].Pawn;
        }

        var known = (state.GetValueOrDefault(m.Kiter) ?? new CombatPawnState()).Cell.TryGetValue(out var at);
        PawnId chaser = default;
        long near = -1;
        long leadRange = m.EffectiveTuning().KiteLeadRange;
        foreach (var h in ranked)
        {
            var ok = h.Cell.TryGetValue(out var c);
            if (h.Target == m.Kiter || (ok && known && Distance2(c, at) <= leadRange * leadRange))
            {
                m.Leading = true;
            }

            if (ok && known && (near < 0 || Distance2(c, at) < near))
            {
                chaser = h.Id;
                near = Distance2(c, at);
            }
        }

        if (chaser.IsEmpty)
        {
            chaser = ranked[0].Id;
        }

        var role = m.Roles[i];
        role.Duty = CombatDuty.Kiter;
        role.Target = chaser;
        role.Cell = null;
        role.Retreat = false;
        if (m.Leading)
        {
            role.Cell = lead.Lure;
            role.Retreat = true;
        }
    }

    /// <summary>The wiki's kiter: a gunner with a long-range gun, no more than light armor, and a MoveSpeed of at least <paramref name="need"/>.</summary>
    private static bool KiterEligible(CombatRole r, CombatPawnState s, Fact<double> armor, double need)
    {
        if (armor.TryGetValue(out var a) && a > KiteMaxArmor)
        {
            return false;
        }

        return r.Ranged && s.WeaponRange >= KiteMinRange && s.MoveSpeed > 0 && s.MoveSpeed >= need;
    }

    /// <summary>
    /// The kiting step's lead cell and the MoveSpeed a kiter needs, null when the fight is not kited. A
    /// manhunter pack of known speeds is led to the inner-line cell farthest behind the firing line; the kiter
    /// needs KiteSpeedRatio of the fastest animal (an infestation's insects are lured out and kited alike),
    /// KiteFastRatio when any animal is at least a colonist's speed. A mech raid takes MechKiteLead.
    /// </summary>
    private static (Cell Lure, double Need)? KiteLead(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Manhunter && m.Tactic != CombatTactic.Infestation)
        {
            return MechKiteLead(view, m);
        }

        if (RearmostRetreat(view) is not { } lure)
        {
            return null;
        }

        var fastest = 0.0;
        var ratio = m.EffectiveTuning().KiteSpeedRatio;
        foreach (var h in RankThreats(view))
        {
            if (h.MoveSpeed <= 0)
            {
                return null;
            }

            if (!ClassifyAnimal(h).Slow)
            {
                ratio = KiteFastRatio;
            }

            fastest = Math.Max(fastest, h.MoveSpeed);
        }

        return (lure, ratio * fastest);
    }

    /// <summary>
    /// KiteLead for a mech raid: unsupported slow mechs (centipedes, breachers) are kited like a slow pack.
    /// Every live mech must be slower than a colonist, so a raid with fast support (scythers) is never kited.
    /// Under the hold the lead cell is the inner-line cell farthest behind the line; under the sapper tactic
    /// it is the predicted breach's first gunner post, inside the room.
    /// </summary>
    private static (Cell Lure, double Need)? MechKiteLead(CombatView view, CombatMemory m)
    {
        if ((m.Tactic != CombatTactic.Hold && m.Tactic != CombatTactic.Sapper) || !MechRaid(view))
        {
            return null;
        }

        var fastest = 0.0;
        foreach (var h in RankThreats(view))
        {
            if (h.MoveSpeed <= 0 || h.MoveSpeed >= ColonistMoveSpeed)
            {
                return null;
            }

            fastest = Math.Max(fastest, h.MoveSpeed);
        }

        var need = m.EffectiveTuning().KiteSpeedRatio * fastest;
        if (m.Tactic == CombatTactic.Hold)
        {
            return RearmostRetreat(view) is { } lure ? (lure, need) : null;
        }

        if (PredictBreach(view) is not { } b)
        {
            return null;
        }

        var posts = BreachPosts(b, SapperInset, SapperSpread, 1);
        if (posts.Count == 0)
        {
            return null;
        }

        return (posts[0], need);
    }

    /// <summary>The layout's inner-line cell farthest behind the firing line; null without a layout, a direction or an inner line.</summary>
    private static Cell? RearmostRetreat(CombatView view)
    {
        var ok = view.Layout.TryGetValue(out var layout);
        if (!ok)
        {
            return null;
        }

        var (v, vok) = TowardVector(layout!.Toward);
        if (!vok || layout.Retreat.Count == 0)
        {
            return null;
        }

        var lure = layout.Retreat.Items[0];
        foreach (var c in layout.Retreat.Items.Skip(1))
        {
            if ((c.X * v.X) + (c.Z * v.Z) > (lure.X * v.X) + (lure.Z * v.Z))
            {
                lure = c;
            }
        }

        return lure;
    }
}
