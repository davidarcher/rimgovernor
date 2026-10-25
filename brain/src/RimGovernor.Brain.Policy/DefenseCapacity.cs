namespace RimGovernor.Brain.Policy;

/// <summary>
/// One colonist's defense strength as the pawn rows read it: whether it can fight now (living, not downed, capable
/// of violence), its MeleeDPS times summary health and its primary's ranged DPS at its hit factor (#3845).
/// </summary>
public sealed record DefenderStrength
{
    public required PawnId Id { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    public required bool ViolenceCapable { get; init; }

    public required double MeleePower { get; init; }

    public required double RangedDps { get; init; }

    /// <summary>Whether the colonist adds to the colony's strength now.</summary>
    public bool Fighting => !Dead && !Downed && ViolenceCapable;
}

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
    /// DPS. Both inputs are total same-frame censuses.
    /// </summary>
    public static double DefenseCapacity(EquatableArray<DefenderStrength> defenders, EquatableArray<DefenseTurretFacts> turrets)
    {
        var strength = 0.0;
        foreach (var d in defenders.Where(d => d.Fighting))
        {
            strength += d.MeleePower + d.RangedDps;
        }
        foreach (var t in turrets)
        {
            if (t.Powered)
            {
                strength += t.Dps;
            }
        }
        return strength * DefensePointsPerDps;
    }
}
