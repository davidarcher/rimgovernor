namespace RimGovernor.Brain.Policy;


public static partial class CombatBleedRules
{
    /// <summary>The BloodLoss severity whose consciousness loss downs a pawn; death is at 1.0.</summary>
    private const double BleedDownSeverity = 0.6;

    /// <summary>
    /// A hostile the fight can leave alone: it is fleeing or its raid is leaving, or no colonist is inside its
    /// weapon range, or every other hostile is downed, dead or fleeing. A hostile targeting a colonist is
    /// never contained: colonist safety wins.
    /// </summary>
    public static bool Contained(CombatView view, PawnId id)
    {
        var colonists = new HashSet<PawnId>(view.Defenders.Items.Select(d => d.Id));
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        // A hostile with no pawn state in the view is not one the fight can judge contained.
        if (!state.TryGetValue(id, out var s) || (s.Presence is CombatPresence.Engaged { Row.Target: var target } && colonists.Contains(target)))
        {
            return false;
        }

        bool Fleeing(PawnId h)
        {
            foreach (var t in view.Positional.Items)
            {
                if (t.Id == h)
                {
                    return t.LordToilClass.TryGetValue(out var toil) && CombatPodsRules.PodStrikeToils.Contains(toil);
                }
            }

            return false;
        }

        if (Fleeing(id))
        {
            return true;
        }

        foreach (var t in view.Positional.Items)
        {
            if (t.Id != id)
            {
                continue;
            }

            if (t.NearestColonistDistance.TryGetValue(out var d) && s.WeaponFacts.Reach > 0 && d > s.WeaponFacts.Reach)
            {
                return true;
            }
        }

        foreach (var t in view.Threats.Items)
        {
            var h = CombatDecideRules.ThreatPawn(t);
            if (h == id || t.Building)
            {
                continue;
            }

            var hs = state.GetValueOrDefault(h);
            if (!t.Dead && !t.Downed && !(hs?.Dead ?? false) && !(hs?.Downed ?? false) && !Fleeing(h))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A hostile whose blood loss crosses the downing point before it kills: the game's death-on-downed roll
    /// does not run on a blood-loss downing, so it survives to be captured. A pawn not bleeding out is false.
    /// </summary>
    private static bool WillBleedDown(CombatPawnState s)
    {
        var loss = s.BloodLoss;
        var rate = s.BleedRatePerDay;
        if (s.BleedOut is not BleedOut.Within { Hours: var death } || rate <= 0 || loss >= 1)
        {
            return false;
        }

        return Math.Max(0, (BleedDownSeverity - loss) / rate * 24) < death;
    }

    /// <summary>The hostiles the fight holds fire on: below the population target, a live humanlike that is contained and will bleed down.</summary>
    internal static HashSet<PawnId> SparedBleeders(CombatView view)
    {
        var output = new HashSet<PawnId>();
        if (view.Population >= FindingConstants.PopulationTarget)
        {
            return output;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        foreach (var t in view.Threats.Items)
        {
            var id = CombatDecideRules.ThreatPawn(t);
            if (!state.TryGetValue(id, out var s) || t.Building || !t.Humanlike || t.Dead || t.Downed || s.Dead || s.Downed)
            {
                continue;
            }

            if (WillBleedDown(s) && Contained(view, id))
            {
                output.Add(id);
            }
        }

        return output;
    }

    /// <summary>
    /// Sends melee at the contained raiders who will not bleed down: below the population target, low-damage
    /// hits make a pain downing likelier than a lethal wound. The attackers are the free defenders without a
    /// gun (unarmed or melee-armed; nobody is unequipped), lowest melee power first, dealt round-robin over
    /// the raiders so several weak pawns share each one. A free defender holds no role, or a plain role on one
    /// of these raiders or on nothing; any cell, duty or other target keeps the pawn where it is needed.
    /// Gunners on a raider that gets finishers drop it; holdFire holds them once the melee locks.
    /// </summary>
    internal static void FinishContained(CombatView view, CombatMemory next)
    {
        if (view.Population >= FindingConstants.PopulationTarget)
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var finish = new HashSet<PawnId>();
        var raiders = new List<PawnId>();
        foreach (var t in view.Threats.Items)
        {
            var id = CombatDecideRules.ThreatPawn(t);
            if (!state.TryGetValue(id, out var s) || t.Building || !t.Humanlike || t.Dead || t.Downed || s.Dead || s.Downed)
            {
                continue;
            }

            if (!WillBleedDown(s) && Contained(view, id))
            {
                finish.Add(id);
                raiders.Add(id);
            }
        }

        if (raiders.Count == 0)
        {
            return;
        }

        var orderable = new HashSet<PawnId>(view.Orderable.Items);
        var role = new Dictionary<PawnId, int>();
        for (var i = 0; i < next.Roles.Count; i++)
        {
            role[next.Roles[i].Pawn] = i;
        }

        var free = new List<SquadDefenderFacts>();
        foreach (var d in view.Defenders.Items)
        {
            if (!orderable.Contains(d.Id) || !SquadDefenseRules.SquadDefenderEligible(d) || d.RangedEquipped || next.Rescue?.Carrying(d.Id) == true)
            {
                continue;
            }

            if (role.TryGetValue(d.Id, out var i))
            {
                var r = next.Roles[i];
                if (r.Cell is not null || r.Duty != CombatDuty.None || r.Retreat || r.Mortar is not null || r.Ground is not null || (r.Target != default && !finish.Contains(r.Target)))
                {
                    continue;
                }
            }

            free.Add(d);
        }

        // Lowest melee power first, then by id.
        free = PolicySort.ByLess(
            free,
            (a, b) =>
            {
                if (a.MeleePower != b.MeleePower)
                {
                    return a.MeleePower < b.MeleePower;
                }

                return string.CompareOrdinal(a.Id.Value, b.Id.Value) < 0;
            }
        );
        var engaged = new HashSet<PawnId>();
        for (var i = 0; i < free.Count; i++)
        {
            var d = free[i];
            var target = raiders[i % raiders.Count];
            engaged.Add(target);
            if (role.TryGetValue(d.Id, out var j))
            {
                next.Roles[j].Target = target;
                next.Roles[j].Ranged = false;
            }
            else
            {
                next.Roles = EquatableArray.From(next.Roles.Append(new CombatRole { Pawn = d.Id, Target = target }));
            }
        }

        foreach (var r in next.Roles)
        {
            if (r.Ranged && engaged.Contains(r.Target))
            {
                r.Target = default;
            }
        }
    }

    /// <summary>That every live threat left is spared: gunners then hold fire rather than pick the spared hostile on fire-at-will.</summary>
    internal static bool OnlySpared(CombatView view, HashSet<PawnId> spared)
    {
        if (spared.Count == 0)
        {
            return false;
        }

        var down = CombatManhunterRules.DownPawns(view);
        foreach (var t in view.Threats.Items)
        {
            if (!t.Dead && !t.Downed && !down.Contains(CombatDecideRules.ThreatPawn(t)))
            {
                return false;
            }
        }

        return true;
    }
}
