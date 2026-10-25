namespace RimGovernor.Brain.Policy;


public static class ArmoryShellsRules
{
    // Shells per built mortar: a base stock, doubled at the fabrication tier. EMP is stocked only when mech
    // threats are plausible, read as a threat tier of fabrication (raid points at armoryFabricationPoints and
    // up, the band where mechanoid raids and clusters become routine).
    internal const long ShellHEPerMortar = 10;
    internal const long ShellIncendiaryPerMortar = 5;
    internal const long ShellEMPPerMortar = 5;

    /// <summary>
    /// The shell stock the armory keeps for the built mortars under assessment <paramref name="a"/>: none
    /// without a mortar, and none of a kind the load's shells have no def for.
    /// </summary>
    public static IReadOnlyList<Amount> MortarShellTargets(int mortars, ArmoryAssessment a, MortarShells shells)
    {
        if (mortars <= 0)
        {
            return [];
        }

        long scale = mortars;
        if (a.Tier >= ArmoryTier.Fabrication)
        {
            scale *= 2;
        }

        var targets = new List<Amount>();

        void Add(ShellKind kind, long perMortar)
        {
            var def = shells.Def(kind);
            if (def.Length != 0)
            {
                targets.Add(new Amount(new Resource(def), perMortar * scale));
            }
        }

        Add(ShellKind.HE, ShellHEPerMortar);
        Add(ShellKind.Incendiary, ShellIncendiaryPerMortar);
        if (a.Threat >= ArmoryTier.Fabrication)
        {
            Add(ShellKind.EMP, ShellEMPPerMortar);
        }

        return targets;
    }

    /// <summary>
    /// Whether any target's stock is below half its count, the point a native stock-target bill resumes at:
    /// above it a standing bill has nothing to add. No targets is false.
    /// </summary>
    public static Fact<bool> ShellsShort(IReadOnlyList<Amount> targets, IReadOnlyDictionary<Resource, long> stock) =>
        Known(targets.Any(t => stock.GetValueOrDefault(t.Resource) * 2 < t.Count));
}
