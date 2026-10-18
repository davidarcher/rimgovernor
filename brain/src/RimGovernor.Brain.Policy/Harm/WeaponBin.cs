namespace RimGovernor.Brain.Policy;

/// <summary>The armor stat family a damage type is resolved against (<c>DamageDef.armorCategory</c>).</summary>
public enum ArmorCategory
{
    Sharp = 0,
    Blunt,
    Heat,
}

/// <summary>
/// One weapon bin of a raid: the per-hit damage, the armor penetration, the armor category it is resolved against, whether
/// it is a ranged hit, and the bin's share (<see cref="Weight"/>) of the composition. #3540 defines the bin; the raid
/// composition (#3542) fills bins from the weapon defs.
/// </summary>
public sealed record WeaponBin
{
    public double Damage { get; init; }
    public double ArmorPenetration { get; init; }
    public ArmorCategory Category { get; init; }
    public bool Ranged { get; init; }
    public double Weight { get; init; }
}
