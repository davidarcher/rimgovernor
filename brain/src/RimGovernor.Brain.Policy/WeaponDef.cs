namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a weapon's def rows say about how it fights: derived once per load from the ThingDef, its verbs and
/// projectile, the projectile's DamageDef and the melee tools' maneuvers. The default value is no weapon (an
/// unarmed pawn).
/// </summary>
public sealed record WeaponDef
{
    /// <summary>A weapon with a non-melee verb.</summary>
    public bool Ranged { get; init; }

    /// <summary>A weapon with only tools.</summary>
    public bool Melee { get; init; }

    /// <summary>The ranged verb's range in cells, 0 for a melee weapon.</summary>
    public double Range { get; init; }

    /// <summary>How far the wielder strikes: Range for a ranged weapon, the longest melee verb range of its tools for a melee weapon, 0 for no weapon.</summary>
    public double Reach { get; init; }

    /// <summary>Membership in the Weapons thing category (what a stockpile calls a weapon): a wood log or a beer is a melee weapon by Melee but not by trade.</summary>
    public bool ByTrade { get; init; }

    /// <summary>A ranged verb that scatters (forcedMissRadius) a projectile that explodes: a grenade, a rocket, a launcher shell.</summary>
    public bool Explosive { get; init; }

    /// <summary>An Explosive weapon fired by a shoot verb (a launcher) rather than thrown (a grenade).</summary>
    public bool Launcher { get; init; }

    /// <summary>A weapon spent by its shot (a rocket launcher).</summary>
    public bool OneUse { get; init; }

    /// <summary>The explosion radius in cells of a weapon a carrier can aim at the ground to hurt a crowd: Explosive, reusable, and its damage hurts or stuns. 0 for every other weapon.</summary>
    public double Blast { get; init; }

    /// <summary>A projectile whose damage stuns and applies to mechanoids.</summary>
    public bool EMP { get; init; }

    /// <summary>A projectile that sets fires.</summary>
    public bool Incendiary { get; init; }

    /// <summary>A melee weapon every tool of which deals blunt-armored damage.</summary>
    public bool Blunt { get; init; }

    /// <summary>
    /// The weapon's nominal damage per second, read from the verb, tool, projectile and damage rows. A planning
    /// estimate, not a prediction of native combat damage; 0 for a def that states none.
    /// </summary>
    public double DPS { get; init; }

    /// <summary>The weapon's armor penetration.</summary>
    public double AP { get; init; }

    /// <summary>An accuracy curve that peaks at medium range or beyond.</summary>
    public bool Precision { get; init; }

    /// <summary>A verb that scatters its shots (forcedMissRadius &gt; 0): area fire a lone fighter alone may carry.</summary>
    public bool ForcedMiss { get; init; }

    /// <summary>
    /// The vanilla hunting rule on the def's rows: a ranged weapon that neither scatters an exploding projectile
    /// nor sets fires. It is the one predicate behind the hunt channel's gunner count and the formation's gunners.
    /// </summary>
    public bool Hunts() => Ranged && !Explosive && !Incendiary;

    /// <summary>An incendiary weapon a carrier aims at the ground: the burn-out's flame thrower (a molotov), which belongs to the burn-out alone.</summary>
    public bool Burner() => Incendiary && Blast > 0;

    /// <summary>
    /// An ordinary arm the armory may craft for a fighter: a weapon by trade that strikes or shoots, and is neither
    /// a scattering explosive, an incendiary nor spent by its shot.
    /// </summary>
    public bool Armament() => ByTrade && (Ranged || Melee) && !Explosive && !Incendiary && !OneUse && !ForcedMiss;
}
