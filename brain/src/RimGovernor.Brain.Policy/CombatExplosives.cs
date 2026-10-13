namespace RimGovernor.Brain.Policy;

// Go: combat_explosives.go
//
// Heavy explosives raids: once any live hostile carries an explosive weapon (threat tier 0) the line spreads
// two tiles apart (firingGap), takes its cells for room to move rather than cover, and posts outside the
// explosives' reach first, so gunners with the longer weapon focus the grenadiers from beyond their range.
// Shield-belt melee fighters charge the rocketeers.

public static partial class PolicyFunctions
{
    /// <summary>The live hostiles carrying an explosive weapon, by threat rank.</summary>
    private static List<CombatPawnState> ExplosiveHostiles(CombatView view) =>
        [.. RankThreats(view).Where(h => ThreatTier(h, new HashSet<PawnId>()) == ThreatExplosive)];

    /// <summary>Carries a launcher (an explosive weapon fired by a shoot verb) rather than a grenade.</summary>
    private static bool Rocketeer(CombatPawnState h) => h.WeaponFacts.Launcher;

    /// <summary>
    /// Formation's candidate cells against explosives: the inner line joins the candidates, cover is not
    /// ranked, and the cells outside every explosive hostile's known range come first.
    /// </summary>
    private static List<Cell> ExplosiveCells(CombatView view, List<Cell> cells, IReadOnlyList<CombatPawnState> explosive)
    {
        if (view.Layout.TryGetValue(out var layout))
        {
            foreach (var c in layout.Retreat.Items)
            {
                if (!cells.Contains(c))
                {
                    cells.Add(c);
                }
            }
        }

        bool Reached(Cell c) =>
            explosive.Any(h => h.Cell.TryGetValue(out var at) && h.WeaponRange > 0 && Math.Sqrt((((double)c.X - at.X) * ((double)c.X - at.X)) + (((double)c.Z - at.Z) * ((double)c.Z - at.Z))) <= h.WeaponRange);

        var output = cells.Where(c => !Reached(c)).ToList();
        foreach (var c in cells)
        {
            if (Reached(c))
            {
                output.Add(c);
            }
        }

        return output;
    }

    /// <summary>
    /// Sends every shield-belt melee fighter at the nearest live rocketeer; the belt stops the shots on the way
    /// in and a launcher cannot fire at a pawn in melee with it.
    /// </summary>
    private static void ExplosivesCharge(CombatView view, CombatMemory m)
    {
        var rockets = ExplosiveHostiles(view).Where(Rocketeer).ToList();
        if (rockets.Count == 0)
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        foreach (var r in m.Roles)
        {
            state.TryGetValue(r.Pawn, out var s);
            if (s is null || !s.ShieldBelt || r.Ranged || r.Mortar is not null)
            {
                continue;
            }

            var target = rockets[0].Id;
            if (s.Cell.TryGetValue(out var at))
            {
                long best = -1;
                foreach (var h in rockets)
                {
                    if (h.Cell.TryGetValue(out var c) && (best < 0 || Distance2(at, c) < best))
                    {
                        target = h.Id;
                        best = Distance2(at, c);
                    }
                }
            }

            r.Cell = null;
            r.Home = null;
            r.Duty = CombatDuty.None;
            r.Retreat = false;
            r.Target = target;
        }
    }
}
