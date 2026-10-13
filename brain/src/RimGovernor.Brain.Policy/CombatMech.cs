namespace RimGovernor.Brain.Policy;

// Go: combat_mech.go, combat_mech_centipede.go

public static partial class PolicyFunctions
{
    /// <summary>Reports a raid of mechanoids: at least one live hostile, and every live hostile a mechanoid.</summary>
    internal static bool MechRaid(CombatView view)
    {
        var ranked = RankThreats(view);
        return ranked.Count > 0 && ranked.All(h => h.Mech);
    }

    /// <summary>The view's positional facts with each mechanoid marked, so a mech raid's assault lord gets the hold.</summary>
    internal static List<DefensiveThreatFacts> MarkMechs(CombatView view)
    {
        var mech = MechKinds(view);
        return [.. view.Positional.Items.Select(t => t with { Mech = mech.GetValueOrDefault(new PawnId(t.Id)) })];
    }

    /// <summary>The view's squad threats with each mechanoid marked, so a mech raid without a hold still forms a squad.</summary>
    internal static List<SquadThreatFacts> MarkSquadMechs(CombatView view)
    {
        var mech = MechKinds(view);
        return [.. view.Threats.Items.Select(t => t with { Mech = mech.GetValueOrDefault(ThreatPawn(t)) })];
    }

    private static Dictionary<PawnId, bool> MechKinds(CombatView view)
    {
        var mech = new Dictionary<PawnId, bool>();
        foreach (var p in view.Pawns.Items)
        {
            mech[p.Id] = p.Mech;
        }

        return mech;
    }

    /// <summary>
    /// How many empty tiles Formation keeps between firing cells: two against a live centipede or any
    /// explosive weapon, whose area blasts hit packed shooters together, else one.
    /// </summary>
    internal static int FiringGap(CombatView view)
    {
        foreach (var h in RankThreats(view))
        {
            if (h.Kind.StartsWith("Mech_Centipede", StringComparison.Ordinal) || ThreatTier(h, new HashSet<PawnId>()) == ThreatExplosive)
            {
                return 2;
            }
        }

        return 1;
    }
}
