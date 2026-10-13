namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Converts observed combat strength (damage per second) into raid-point units. Vanilla prices a pirate gunner
    /// carrying an assault rifle (about 11 DPS on the wire) at 50-65 combat power, and an unarmed raider brawls
    /// near 2-3 melee DPS for 35-ish points, so about 5 points per DPS lines the colony's measure up with the
    /// storyteller's raid budget.
    /// </summary>
    public const double DefensePointsPerDps = 5.0;

    /// <summary>
    /// The colony's combat strength in raid-point units: each combat-ready colonist (living, not downed, capable
    /// of violence) adds melee power plus ranged weapon DPS, and every powered turret adds its DPS. Any unknown
    /// input a sum needs leaves the result unknown.
    /// </summary>
    public static Fact<double> DefenseCapacity(Fact<EquatableArray<SquadDefenderFacts>> defenders, Fact<EquatableArray<DefenseTurretFacts>> turrets)
    {
        if (!defenders.TryGetValue(out var pawns) || !turrets.TryGetValue(out var guns))
        {
            return Unknown<double>();
        }
        var strength = 0.0;
        foreach (var d in pawns)
        {
            if (!d.Dead.TryGetValue(out var dead) || !d.Downed.TryGetValue(out var downed) || !d.ViolenceCapable.TryGetValue(out var violent))
            {
                return Unknown<double>();
            }
            if (dead || downed || !violent)
            {
                continue;
            }
            if (!d.MeleePower.TryGetValue(out var melee) || !d.RangedDps.TryGetValue(out var ranged))
            {
                return Unknown<double>();
            }
            strength += melee + ranged;
        }
        foreach (var t in guns)
        {
            if (!t.Powered.TryGetValue(out var powered))
            {
                return Unknown<double>();
            }
            if (!powered)
            {
                continue;
            }
            if (!t.Dps.TryGetValue(out var dps))
            {
                return Unknown<double>();
            }
            strength += dps;
        }
        return Known(strength * DefensePointsPerDps);
    }
}
