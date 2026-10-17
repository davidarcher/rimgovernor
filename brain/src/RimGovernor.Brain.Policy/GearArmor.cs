namespace RimGovernor.Brain.Policy;

/// <summary>
/// One step of an armor family's ladder: the definition and the lowest armory tier that may craft it. Rungs run
/// from the cheapest to the best; a family's need falls back down its rungs when the tier cannot reach the higher
/// one. (Go <c>armoryArmorRung</c>.)
/// </summary>
internal readonly record struct ArmoryArmorRung(Resource Definition, ArmoryTier Tier);

public static class GearArmorRules
{
    /// <summary>The body-armor and helmet ladder the armory owns: flak, recon, marine, with the simple helmet at smithing.</summary>
    private static readonly IReadOnlyList<IReadOnlyList<ArmoryArmorRung>> ArmoryArmorFamilies =
    [
        [
            new(new Resource("Apparel_FlakVest"), ArmoryTier.Machining),
            new(new Resource("Apparel_FlakJacket"), ArmoryTier.Machining),
            new(new Resource("Apparel_ArmorRecon"), ArmoryTier.Fabrication),
            new(new Resource("Apparel_PowerArmor"), ArmoryTier.Fabrication),
        ],
        [new(new Resource("Apparel_FlakPants"), ArmoryTier.Machining)],
        [
            new(new Resource("Apparel_SimpleHelmet"), ArmoryTier.Smithing),
            new(new Resource("Apparel_AdvancedHelmet"), ArmoryTier.Machining),
            new(new Resource("Apparel_ArmorHelmetRecon"), ArmoryTier.Fabrication),
            new(new Resource("Apparel_PowerArmorHelmet"), ArmoryTier.Fabrication),
        ],
    ];

    /// <summary>
    /// The rungs a need for definition may be crafted as under tier, best first: the need's own rung and every
    /// lower one the tier allows. Not ok for a definition outside the armory's ladder.
    /// </summary>
    internal static (IReadOnlyList<Resource> Rungs, bool Ok) ArmoryArmorRungs(Resource definition, ArmoryTier tier)
    {
        foreach (var family in ArmoryArmorFamilies)
        {
            for (var i = 0; i < family.Count; i++)
            {
                if (family[i].Definition != definition)
                {
                    continue;
                }
                var rungs = new List<Resource>();
                for (var j = i; j >= 0; j--)
                {
                    if (family[j].Tier <= tier)
                    {
                        rungs.Add(family[j].Definition);
                    }
                }
                return (rungs, true);
            }
        }
        return ([], false);
    }

    /// <summary>Reports whether definition is body armor or a helmet the armory crafts; the gear planner leaves its bills to the armory.</summary>
    public static bool ArmoryArmor(Resource definition)
    {
        var (_, ok) = ArmoryArmorRungs(definition, ArmoryTier.Fabrication);
        return ok;
    }
}
