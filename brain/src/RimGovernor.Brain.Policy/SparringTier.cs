namespace RimGovernor.Brain.Policy;

/// <summary>
/// One rung of the melee practice ladder: a practice weapon the spar job issues. The table mirrors the
/// SparringTier DefModExtension on each weapon def in
/// integrations/rimgovernor-native/Defs/ThingDefs/SparringRing.xml, which owns the values. Damage is flat per
/// damage type and vanilla pays the melee XP, so a tier buys a higher ceiling and nothing else.
/// </summary>
/// <param name="Weapon">The weapon def name.</param>
/// <param name="DamageType">The weapon's one tool's damage type.</param>
/// <param name="Power">The tool's power: shared by weapons of one damage type.</param>
/// <param name="Ceiling">The skill level at which the weapon stops teaching.</param>
/// <param name="Gate">The research project that unlocks the tier; empty means none.</param>
/// <param name="Apparel">The practice apparel issued with the weapon, by def name.</param>
public sealed record SparringTier(string Weapon, string DamageType, int Power, int Ceiling, string Gate, EquatableArray<string> Apparel)
{
    private const string PracticeTunic = "Apparel_PracticeTunic";
    private const string PracticeHelmet = "Apparel_PracticeHelmet";
    private const string PracticeGloves = "Apparel_PracticeGloves";
    private const string PracticeVest = "Apparel_PracticeVest";

    /// <summary>The ladder, tier 0 first.</summary>
    public static readonly IReadOnlyList<SparringTier> SparringTiers =
    [
        new("MeleeWeapon_PracticeClub", "Blunt", 3, 8, "", EquatableArray.From([PracticeTunic])),
        new("MeleeWeapon_PracticeSword", "Cut", 3, 12, "Smithing", EquatableArray.From([PracticeTunic, PracticeHelmet, PracticeGloves])),
        new("MeleeWeapon_PracticeSwordBetter", "Cut", 3, 16, "Machining", EquatableArray.From([PracticeTunic, PracticeHelmet, PracticeGloves, PracticeVest])),
        new("MeleeWeapon_PracticeSwordBest", "Cut", 3, 20, "Fabrication", EquatableArray.From([PracticeTunic, PracticeHelmet, PracticeGloves, PracticeVest])),
    ];

    /// <summary>
    /// The best tier whose gate research is finished; tier 0 has no gate, so an unknown census holds at the
    /// club. The native rules read the same census through the same rule (SparringRules.UnlockedWeapon).
    /// </summary>
    public static SparringTier UnlockedSparringTier(Fact<ResearchFacts> research)
    {
        var best = SparringTiers[0];
        if (!research.TryGetValue(out var facts))
        {
            return best;
        }
        foreach (var tier in SparringTiers.Skip(1))
        {
            if (facts.Finished.Contains(new ResearchProjectId(tier.Gate)))
            {
                best = tier;
            }
        }
        return best;
    }
}
