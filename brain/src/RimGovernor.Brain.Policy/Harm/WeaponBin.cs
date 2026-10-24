namespace RimGovernor.Brain.Policy;

/// <summary>The armor stat family a damage type is resolved against (<c>DamageDef.armorCategory</c>).</summary>
public enum ArmorCategory
{
    Sharp = 0,
    Blunt,
    Heat,
}

/// <summary>
/// One weapon bin of a raid: the per-hit damage, the armor penetration, the armor category it is resolved against,
/// and the bin's share (<see cref="Weight"/>) of the composition. #3540 defines the bin; the raid
/// composition (#3542) fills bins from the weapon defs.
/// </summary>
public sealed record WeaponBin
{
    public double Damage { get; init; }
    public double ArmorPenetration { get; init; }
    public ArmorCategory Category { get; init; }
    public double Weight { get; init; }

    /// <summary>Ranged weapons' bin: a projectile hit lands at any depth, a melee one on an Outside part (#3847).</summary>
    public bool Ranged { get; init; }

    /// <summary>The damage defs the bin's hits deal, their shares summing to 1 (#3847): the members' <see cref="WeaponDef.Damages"/> by weight.</summary>
    public EquatableArray<WeaponDamage> Damages { get; init; }
}

/// <summary>One damage def a weapon's hits deal and its share of them (#3847).</summary>
public sealed record WeaponDamage(string Def, double Share);
