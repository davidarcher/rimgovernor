using RimGovernor.Brain.Domain;
using Defs = RimGovernor.Protocol.Defs;

namespace RimGovernor.Brain.Simulation.Combat.Import;

// Weapon stat names and the game's constants the effective weapon values are read with.
internal sealed partial class Importer
{
    private const string StatCooldown = "RangedWeapon_Cooldown";
    private const string StatDamageMult = "RangedWeapon_DamageMultiplier";
    private const string StatPenetrationMult = "RangedWeapon_ArmorPenetrationMultiplier";
    private const string ShootVerbClass = "Verse.Verb_Shoot";
    private const string MeleeVerbClass = "RimWorld.Verb_MeleeAttack";

    // ProjectileProperties.GetArmorPenetration's fallback for a projectile that states none, per point of damage.
    private const double PenetrationPerDamage = 0.015;

    // Divides ProjectileProperties.speed into SpeedTilesPerTick.
    private const double ProjectileSpeedUnit = 100;

    /// <summary>
    /// The ranged damage def every weapon in the Spec deals. The injury thresholds and the armor list depend on
    /// it, so a Spec mixing damage kinds is unsupported (A25).
    /// </summary>
    private sealed record DamageInfo
    {
        public string Def { get; init; } = "";

        public string ArmorCategory { get; init; } = "";

        public string Hediff { get; init; } = "";

        /// <summary>The armor category's armorRatingStat, empty for damage that no armor rates.</summary>
        public string ArmorStat { get; init; } = "";

        /// <summary>The damage def's harmAllLayersUntilOutside.</summary>
        public bool HarmAllLayers { get; init; }

        /// <summary>The damage def's overkillPctToDestroyPart range.</summary>
        public double OverkillLow { get; init; }

        public double OverkillHigh { get; init; }
    }

    /// <summary>One pawn's weapon resolved.</summary>
    private sealed record ResolvedWeapon
    {
        public WeaponDef? Ranged { get; init; }

        /// <summary>The catalog DPS of a melee weapon the sim cannot fire.</summary>
        public double MeleeDps { get; init; }

        public bool Melee { get; init; }
    }

    // Resolves every pawn's weapon and the Spec's one damage def.
    private ResolvedWeapon[] Weapons(Spec spec)
    {
        var @out = new ResolvedWeapon[spec.Pawns.Count];
        Array.Fill(@out, new ResolvedWeapon());
        for (var i = 0; i < spec.Pawns.Count; i++)
        {
            var p = spec.Pawns[i];
            if (p.Weapon.Length == 0)
            {
                continue;
            }
            Policy.WeaponDef facts;
            try
            {
                facts = cat.WeaponOf(p.Weapon);
            }
            catch (DomainException e)
            {
                throw Unsupported($"pawn \"{p.Id}\" weapon \"{p.Weapon}\": {e.Message}");
            }
            if (facts.Ranged)
            {
                if (facts.Explosive || facts.Launcher || facts.OneUse || facts.EMP || facts.Incendiary)
                {
                    throw Unsupported($"pawn \"{p.Id}\" weapon \"{p.Weapon}\" is not ordinary ranged fire");
                }
                @out[i] = new ResolvedWeapon { Ranged = RangedWeapon(p.Id.Value, p.Weapon, p.WeaponStuff) };
            }
            else if (facts.Melee)
            {
                @out[i] = new ResolvedWeapon { Melee = true, MeleeDps = facts.DPS };
                _unmodeled.Add($"pawn {p.Id} wields melee weapon {p.Weapon}: the sim has no melee, the pawn is imported unarmed");
            }
            else
            {
                throw Unsupported($"pawn \"{p.Id}\" weapon \"{p.Weapon}\" is neither ranged nor melee");
            }
        }
        if (_dmg is null)
        {
            throw Unsupported("no pawn wields an ordinary ranged weapon: there is no damage to derive injury thresholds from");
        }
        return @out;
    }

    // Reads one weapon's effective values: the verb's range, warmup and burst from its row, the cooldown, accuracy
    // bands and damage multipliers from the weapon's stats (normal quality), the projectile's damage and speed
    // from its row (A27).
    private WeaponDef RangedWeapon(string pawn, string def, string stuff)
    {
        var row = cat.ThingDef(def) ?? throw Unsupported($"catalog has no weapon def \"{def}\"");
        Defs.VerbProperties? verb = null;
        foreach (var entry in row.Verbs)
        {
            var cls = entry.Value?.VerbClass ?? "";
            if (!ClassIsA(cls, MeleeVerbClass, $"weapon \"{def}\" verb class"))
            {
                verb = entry.Value;
                break;
            }
        }
        if (verb is null)
        {
            throw Unsupported($"weapon \"{def}\" has no ranged verb");
        }
        if (!ClassIsA(verb.VerbClass, ShootVerbClass, $"weapon \"{def}\" verb class"))
        {
            throw Unsupported($"weapon \"{def}\" verb \"{verb.VerbClass}\" is not Verb_Shoot");
        }
        if (verb.RangeStat.Length != 0)
        {
            throw Unsupported($"weapon \"{def}\" verb range comes from pawn stat \"{verb.RangeStat}\"");
        }
        var props = cat.ThingDef(verb.DefaultProjectile)?.Projectile
            ?? throw Unsupported($"weapon \"{def}\" has no projectile def \"{verb.DefaultProjectile}\"");
        var dd = cat.DefRow<Defs.DamageDef>(props.DamageDef) ?? throw Unsupported($"catalog has no damage def \"{props.DamageDef}\"");
        SettleDamage(props.DamageDef, dd);

        double Stat(string name) => ThingStat(name, def, stuff, $"weapon \"{def}\" stat {name}");

        var range = (double)verb.Range;
        if (_weatherCap >= 0)
        {
            range = Math.Min(range, _weatherCap);
        }
        var cooldown = Stat(StatCooldown);
        var touch = Stat("AccuracyTouch");
        var near = Stat("AccuracyShort");
        var medium = Stat("AccuracyMedium");
        var far = Stat("AccuracyLong");

        // ProjectileProperties.GetDamageAmount: the stated amount or the damage def's default, times the weapon's
        // damage multiplier, rounded.
        double amount = props.DamageAmountBase;
        if (props.DamageAmountBase == -1)
        {
            amount = dd.DefaultDamage;
        }
        var mult = Stat(StatDamageMult);
        var damageAmount = (int)Math.Round(amount * mult, MidpointRounding.ToEven);

        // ProjectileProperties.GetArmorPenetration.
        var armorPenetration = 0.0;
        if (dd.ArmorCategory.Length != 0)
        {
            double ap = props.ArmorPenetrationBase;
            if (props.DamageAmountBase == -1 && props.ArmorPenetrationBase < 0)
            {
                ap = dd.DefaultArmorPenetration;
            }
            if (ap < 0)
            {
                ap = amount * PenetrationPerDamage;
            }
            armorPenetration = ap * Stat(StatPenetrationMult);
        }

        var src = "ThingDef " + def;
        var proj = "ThingDef " + verb.DefaultProjectile;
        foreach (var (field, source) in new (string, string)[]
        {
            ("Weapon.Range", src + " verb range, WeatherDef maxRangeCap"),
            ("Weapon.MinRange", src + " verb minRange"),
            ("Weapon.WarmupSeconds", src + " verb warmupTime"),
            ("Weapon.CooldownSeconds", "stat " + StatCooldown),
            ("Weapon.BurstShotCount", src + " verb burstShotCount"),
            ("Weapon.TicksBetweenBurstShots", src + " verb ticksBetweenBurstShots"),
            ("Weapon.AccuracyTouch", "stat AccuracyTouch"),
            ("Weapon.AccuracyShort", "stat AccuracyShort"),
            ("Weapon.AccuracyMedium", "stat AccuracyMedium"),
            ("Weapon.AccuracyLong", "stat AccuracyLong"),
            ("Weapon.ProjectileSpeed", proj + " projectile speed"),
            ("Weapon.DamageAmount", proj + " damageAmountBase or DamageDef " + props.DamageDef + " defaultDamage, stat " + StatDamageMult),
            ("Weapon.ArmorPenetration", proj + " armorPenetrationBase or DamageDef " + props.DamageDef + " defaultArmorPenetration, stat " + StatPenetrationMult),
            ("Weapon.ForcedMissRadius", src + " verb forcedMissRadius"),
        })
        {
            Trace(pawn, field, source);
        }
        return new WeaponDef
        {
            Name = def,
            Range = range,
            MinRange = verb.MinRange,
            WarmupSeconds = verb.WarmupTime,
            CooldownSeconds = cooldown,
            BurstShotCount = verb.BurstShotCount,
            TicksBetweenBurstShots = verb.TicksBetweenBurstShots,
            AccuracyTouch = touch,
            AccuracyShort = near,
            AccuracyMedium = medium,
            AccuracyLong = far,
            ProjectileSpeed = props.Speed / ProjectileSpeedUnit,
            DamageAmount = damageAmount,
            ArmorPenetration = armorPenetration,
            ForcedMissRadius = verb.ForcedMissRadius,
        };
    }

    // Records the Spec's damage def and reads the pain it causes, or fails when a second, different damage def appears.
    private void SettleDamage(string name, Defs.DamageDef dd)
    {
        if (_dmg is not null)
        {
            if (!string.Equals(_dmg.Def, name, StringComparison.Ordinal))
            {
                throw Unsupported($"weapons deal two damage defs (\"{_dmg.Def}\" and \"{name}\"): thresholds and armor are derived for one");
            }
            return;
        }
        if (dd.Hediff.Length == 0)
        {
            throw Unsupported($"damage def \"{name}\" applies no hediff");
        }
        var hediff = cat.DefRow<Defs.HediffDef>(dd.Hediff);
        if (hediff?.InjuryProps is null)
        {
            throw Unsupported($"catalog has no injury hediff \"{dd.Hediff}\" for damage def \"{name}\"");
        }
        if (!(hediff.InjuryProps.PainPerSeverity > 0))
        {
            throw Unsupported($"hediff \"{dd.Hediff}\" causes no pain per severity");
        }
        var overkill = dd.OverkillPctToDestroyPart;
        var armorStat = "";
        if (dd.ArmorCategory.Length != 0)
        {
            var category = cat.DefRow<Defs.DamageArmorCategoryDef>(dd.ArmorCategory);
            if (category is null || category.ArmorRatingStat.Length == 0)
            {
                throw Unsupported($"catalog has no armor category \"{dd.ArmorCategory}\" for damage def \"{name}\"");
            }
            armorStat = category.ArmorRatingStat;
        }
        _dmg = new DamageInfo
        {
            Def = name,
            ArmorCategory = dd.ArmorCategory,
            Hediff = dd.Hediff,
            HarmAllLayers = dd.HarmAllLayersUntilOutside,
            OverkillLow = overkill?.Min ?? 0,
            OverkillHigh = overkill?.Max ?? 0,
            ArmorStat = armorStat,
        };
    }
}
