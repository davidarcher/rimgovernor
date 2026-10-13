namespace RimGovernor.Brain.Policy;

/// <summary>
/// One rung of the shooting practice ladder: a practice weapon the training drill issues, and what it pays. The
/// table mirrors the TrainingTier DefModExtension on each weapon def in
/// integrations/rimgovernor-native/Defs/ThingDefs/TrainingRange.xml, which owns the values.
/// </summary>
/// <param name="Weapon">The weapon def name.</param>
/// <param name="Projectile">The weapon's themed projectile, which deals exactly 1 damage (Thing.TakeDamage returns early at 0).</param>
/// <param name="Multiplier">The XP multiple against tier 0.</param>
/// <param name="XPPerShot">The skill XP one shot applies.</param>
/// <param name="Ceiling">The skill level at which the weapon stops teaching.</param>
/// <param name="Gate">The research project that unlocks the tier; empty means none.</param>
public sealed record TrainingTier(string Weapon, string Projectile, double Multiplier, int XPPerShot, int Ceiling, string Gate)
{
    /// <summary>The ladder, tier 0 first. Every weapon shares the 3 s shot cycle, so XP per shot is XP per cycle.</summary>
    public static readonly IReadOnlyList<TrainingTier> TrainingTiers =
    [
        new(PolicyFunctions.RangeWeaponDef, "Arrow_Practice", 1, 75, 8, ""),
        new("Gun_PracticeRifle", "Bullet_PracticeRubber", 1.5, 112, 12, "Gunsmithing"),
        new("Gun_PracticePulseRifle", "Bullet_PracticePulse", 2.5, 187, 16, "ChargedShot"),
        new("Gun_PracticeBeamEmitter", "Bullet_PracticeBeam", 4, 300, 20, "BeamWeapons"),
    ];

    /// <summary>
    /// The best tier whose gate research is finished; tier 0 has no gate, so an unknown census (as
    /// ArmoryResearchTier) holds at the bow. The native drill reads the same census through the same rule.
    /// </summary>
    public static TrainingTier UnlockedTrainingTier(Fact<ResearchFacts> research)
    {
        var best = TrainingTiers[0];
        if (!research.TryGetValue(out var facts))
        {
            return best;
        }
        foreach (var tier in TrainingTiers.Skip(1))
        {
            if (facts.Finished.Contains(new ResearchProjectId(tier.Gate)))
            {
                best = tier;
            }
        }
        return best;
    }
}
