namespace RimGovernor.Brain.Policy;

public static class DefenseCapacityPolicy
{
    /// <summary>
    /// Converts observed combat strength (damage per second) into raid-point units. Vanilla prices a pirate gunner
    /// carrying an assault rifle (about 11 DPS on the wire) at 50-65 combat power, and an unarmed raider brawls
    /// near 2-3 melee DPS for 35-ish points, so about 5 points per DPS lines the colony's measure up with the
    /// storyteller's raid budget. Ranged DPS is in that gunner's units (#3845): each pawn's counts at its game hit chance
    /// over the reference gunner's (<c>PawnEquipment.RangedHitFactor</c>), so the reference gunner, and every gunner the
    /// exposure calibration staged, counts at 1 and the scale holds.
    /// </summary>
    public const double DefensePointsPerDps = 5.0;

    /// <summary>
    /// The colony's combat strength in raid-point units: each combat-ready colonist (living, not downed, capable
    /// of violence) adds melee power plus ranged weapon DPS weighted by its hit factor, and every powered turret adds its
    /// DPS. Any unknown
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
            if (!d.MeleePower.TryGetValue(out var melee) || !d.RangedDPS.TryGetValue(out var ranged))
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
