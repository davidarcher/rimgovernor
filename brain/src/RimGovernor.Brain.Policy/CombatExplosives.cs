namespace RimGovernor.Brain.Policy;

//
// Heavy explosives raids: once any live hostile carries an explosive weapon (threat tier 0) the line spreads
// two tiles apart (firingGap), takes its cells for room to move rather than cover, and posts outside the
// explosives' reach first, so gunners with the longer weapon focus the grenadiers from beyond their range.
// Shield-belt melee fighters charge the rocketeers.

public static partial class CombatExplosivesRules
{
    /// <summary>The live hostiles carrying an explosive weapon, by threat rank.</summary>
    internal static List<CombatPawnState> ExplosiveHostiles(CombatView view) =>
        [.. CombatThreatRules.RankThreats(view).Where(h => CombatThreatRules.ThreatTier(h, new HashSet<PawnId>()) == CombatThreatRules.ThreatExplosive)];

    /// <summary>Carries a launcher (an explosive weapon fired by a shoot verb) rather than a grenade.</summary>
    private static bool Rocketeer(CombatPawnState h) => h.WeaponFacts.Launcher;

    /// <summary>
    /// Formation's candidate cells against explosives: the inner line joins the candidates, cover is not
    /// ranked, and the cells outside every explosive hostile's known range come first.
    /// </summary>
    internal static List<Cell> ExplosiveCells(CombatView view, List<Cell> cells, IReadOnlyList<CombatPawnState> explosive)
    {
        if (view.Layout is { } layout)
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
            explosive.Any(h => h.Cell is var at && h.WeaponFacts.Reach > 0 && Math.Sqrt((((double)c.X - at.X) * ((double)c.X - at.X)) + (((double)c.Z - at.Z) * ((double)c.Z - at.Z))) <= h.WeaponFacts.Reach);

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
    internal static void ExplosivesCharge(CombatView view, CombatMemory m)
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
            if (!state.TryGetValue(r.Pawn, out var s) || s.Presence is not CombatPresence.Engaged { Row.Shield: ShieldBelt.Worn } || r.Ranged || r.Mortar is not null)
            {
                continue;
            }

            var target = rockets[0].Id;
            var at = s.Cell;
            long best = -1;
            foreach (var h in rockets)
            {
                if (best < 0 || CombatManhunterRules.Distance2(at, h.Cell) < best)
                {
                    target = h.Id;
                    best = CombatManhunterRules.Distance2(at, h.Cell);
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
