using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_weapons.go: what the def rows say of a weapon. The primary-weapon lookup of an observed pawn's
// equipment (PrimaryWeapon) reads a frame, which is wire plumbing; the executor port (#3055) calls WeaponOf with
// the def name the frame resolves.

public sealed partial class DefinitionCatalog
{
    // The thing category a stockpile calls a weapon (what a def sits within, parents included).
    private const string CategoryWeapons = "Weapons";
    private const string BluntArmorCategory = "Blunt";

    private const string StatRangedCooldown = "RangedWeapon_Cooldown";
    private const string StatRangedDamageMult = "RangedWeapon_DamageMultiplier";
    private const string StatRangedPenMult = "RangedWeapon_ArmorPenetrationMultiplier";
    private const string StatMeleeCooldown = "MeleeWeapon_CooldownMultiplier";
    private const string StatMeleeDamageMult = "MeleeWeapon_DamageMultiplier";
    private const string StatAccuracyShort = "AccuracyShort";
    private const string StatAccuracyMedium = "AccuracyMedium";
    private const string StatAccuracyLong = "AccuracyLong";

    // The armor penetration an unstated (negative) tool or projectile penetration falls back to, per point of damage
    // (VerbProperties.AdjustedArmorPenetration, ProjectileProperties).
    private const double PenetrationPerDamage = 0.015;

    /// <summary>
    /// What the def rows say of weapon def <paramref name="name"/>: its ranged verb's range, its projectile's explosion,
    /// damage and fire, whether every melee tool hits blunt, and whether it sits in the Weapons thing category. "" is no
    /// weapon and is the empty WeaponDef; a def without a row, a projectile or a damage def row is an error.
    /// </summary>
    public P.WeaponDef WeaponOf(string name)
    {
        var weapon = new P.WeaponDef();
        if (name.Length == 0)
        {
            return weapon;
        }

        var row = ThingRow(name);
        weapon = weapon with { ByTrade = CategoriesWithin(row).Contains(CategoryWeapons) };
        var ranged = false;
        foreach (var verb in row.Verbs.Seq())
        {
            var melee = verb.verbClass is { } kind && typeof(Verb_MeleeAttack).IsAssignableFrom(kind);
            if (melee || ranged)
            {
                continue;
            }

            ranged = true;
            var oneUse = verb.verbClass is { } use && typeof(Verb_ShootOneUse).IsAssignableFrom(use);
            var shoot = verb.verbClass is { } shot && typeof(Verb_Shoot).IsAssignableFrom(shot);
            weapon = weapon with { Ranged = true, Range = verb.range, Reach = verb.range, OneUse = oneUse };
            if (verb.defaultProjectile is not { } projectile)
            {
                continue;
            }

            var properties = ThingRow(projectile.defName).projectile
                ?? throw new CatalogException($"catalog def {projectile.defName} is the projectile of {name} but has no projectile properties");

            // A projectile that states no damage def (the turret and drone packs' verbs) has no damage to read.
            if (properties.damageDef is not { } damage)
            {
                continue;
            }

            var explosive = GameMembers.ForcedMissRadius(verb) > 0 && properties.explosionRadius > 0;
            weapon = weapon with
            {
                EMP = damage.causeStun && GameMembers.ExternalViolenceForMechanoids(damage),
                Incendiary = properties.ai_IsIncendiary,
                Explosive = explosive,
                Launcher = explosive && shoot,
            };
            if (explosive && !oneUse && (damage.harmsHealth || damage.causeStun))
            {
                weapon = weapon with { Blast = properties.explosionRadius };
            }

            weapon = RangedThroughput(row, verb, properties, damage, weapon);
        }

        if (!ranged && row.tools.Seq().Any())
        {
            var blunt = row.tools.All(tool => tool.capacities.Seq().All(BluntCapacity));
            weapon = MeleeThroughput(row, weapon with { Melee = true, Blunt = blunt });
        }

        return weapon;
    }

    /// <summary>The def's own statBases entry for <paramref name="stat"/>, null when it states none.</summary>
    private static float? StatBaseEntry(ThingDef row, string stat) =>
        row.statBases.Seq().FirstOrDefault(m => m.stat.defName == stat)?.value;

    // StatBase is the game's GetStatValueAbstract(stat) of a weapon def with no stuff: the def's own statBases entry,
    // else the stat def's default.
    private double StatBase(ThingDef row, string stat) =>
        StatBaseEntry(row, stat)
        ?? DefRow<StatDef>(stat)?.defaultBaseValue
        ?? throw new CatalogException($"catalog has no {stat} stat def, the stat of weapon {row.defName}");

    // The damage per second, armor penetration, forced miss and precision of a ranged weapon from its verb,
    // projectile, damage def and stat rows. The DPS is NOT a game formula: it is projectile damage times shots per
    // burst over the verb's full cycle time (warmup + RangedWeapon_Cooldown + the gaps between burst shots), the same
    // cycle VerbProperties.AdjustedFullCycleTime gives a melee verb. Damage and penetration follow ProjectileProperties
    // (GetDamageAmount, GetArmorPenetration) without a wielder.
    private P.WeaponDef RangedThroughput(ThingDef row, VerbProperties verb, ProjectileProperties projectile, DamageDef damage, P.WeaponDef weapon)
    {
        // Precision is an accuracy curve that does not fall off before medium range: the weapon states all three bands
        // and its medium or long band is at least its short one.
        var precision = StatBaseEntry(row, StatAccuracyShort) is { } shortRange
            && StatBaseEntry(row, StatAccuracyMedium) is { } medium
            && StatBaseEntry(row, StatAccuracyLong) is { } longRange
            && Math.Max(medium, longRange) >= shortRange;
        double amount = GameMembers.DamageAmountBase(projectile) == -1 ? damage.defaultDamage : GameMembers.DamageAmountBase(projectile);
        amount = Math.Round(amount * StatBase(row, StatRangedDamageMult), MidpointRounding.AwayFromZero);
        double penetration = 0;
        if (damage.armorCategory is not null)
        {
            penetration = GameMembers.ArmorPenetrationBase(projectile);
            if (GameMembers.DamageAmountBase(projectile) == -1 && GameMembers.ArmorPenetrationBase(projectile) < 0)
            {
                penetration = damage.defaultArmorPenetration;
            }

            if (penetration < 0)
            {
                penetration = amount * PenetrationPerDamage;
            }

            penetration *= StatBase(row, StatRangedPenMult);
        }

        double burst = Math.Max(1, verb.burstShotCount);
        var cycle = verb.warmupTime + StatBase(row, StatRangedCooldown) + ((burst - 1) * verb.ticksBetweenBurstShots / GenTicks.TicksPerRealSecond);
        return weapon with
        {
            ForcedMiss = GameMembers.ForcedMissRadius(verb) > 0,
            Precision = precision,
            AP = penetration,
            DPS = cycle > 0 ? amount * burst / cycle : weapon.DPS,
        };
    }

    // A melee weapon's damage per second and armor penetration. Each tool, through each maneuver of its capacities, is
    // one melee verb whose DPS is the game's VerbUtility.DPS (damage * (1 + armor penetration) * accuracyTouch / full
    // cycle time, the cycle being the maneuver's warmup plus the tool's cooldown and the burst gaps). The weapon's figure
    // averages its verbs by the game's own selection weight (damage squared times the verb's commonality times the
    // tool's chanceFactor), the mix a wielder swings. No stuff is assumed: damage is the tool's power.
    private P.WeaponDef MeleeThroughput(ThingDef row, P.WeaponDef weapon)
    {
        var cooldownFactor = StatBase(row, StatMeleeCooldown);
        double weight = 0, dps = 0, penetration = 0, reach = weapon.Reach;
        foreach (var tool in row.tools)
        {
            double power = tool.power;
            double ap = tool.armorPenetration;
            ap = ap < 0 ? power * PenetrationPerDamage : ap * StatBase(row, StatMeleeDamageMult);
            foreach (var capacity in tool.capacities.Seq())
            {
                foreach (var maneuver in Maneuvers(capacity))
                {
                    var verb = maneuver.verb;
                    reach = Math.Max(reach, verb.range);
                    double burst = Math.Max(1, verb.burstShotCount);
                    var cycle = verb.warmupTime + (tool.cooldownTime * cooldownFactor) + ((burst - 1) * verb.ticksBetweenBurstShots / GenTicks.TicksPerRealSecond);
                    if (cycle <= 0)
                    {
                        throw new CatalogException($"melee verb {maneuver.defName} of weapon {row.defName} has a zero cycle time");
                    }

                    var w = power * power * verb.commonality * tool.chanceFactor;
                    weight += w;
                    dps += w * power * (1 + ap) * verb.accuracyTouch / cycle;
                    penetration += w * ap;
                }
            }
        }

        return weight > 0 ? weapon with { Reach = reach, DPS = dps / weight, AP = penetration / weight } : weapon with { Reach = reach };
    }

    // Every maneuver def of tool capacity: the melee verbs a tool of that capacity has (Tool.Maneuvers).
    private List<ManeuverDef> Maneuvers(ToolCapacityDef capacity)
    {
        var found = Rows<ManeuverDef>().Values.Where(m => m.requiredCapacity == capacity).OrderBy(m => m.defName, StringComparer.Ordinal).ToList();
        return found.Count > 0 ? found : throw new CatalogException($"catalog has no maneuver for tool capacity {capacity.defName}");
    }

    // Whether every maneuver of tool capacity deals damage of the blunt armor category.
    private bool BluntCapacity(ToolCapacityDef capacity) =>
        Maneuvers(capacity).All(m =>
            (m.verb.meleeDamageDef ?? throw new CatalogException($"catalog has no damage def row for the damage of maneuver {m.defName}")).armorCategory?.defName == BluntArmorCategory);
}
