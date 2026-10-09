#nullable enable
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Defense capacity facts: damage per second read from the game's
    // own verbs and stats, never from a fixed table.
    internal static class NativeDefenseStats
    {
        // RangedDps is a ranged verb's damage per second: projectile damage
        // times burst, over one cycle of warmup, cooldown and burst gaps.
        // Null when the verb is not a projectile attack or the cycle is empty.
        internal static double? RangedDps(Verb? verb, Thing? weapon, float warmupSeconds, float cooldownSeconds)
        {
            if (verb == null || verb.IsMeleeAttack) return null;
            var props = verb.verbProps;
            var projectile = props?.defaultProjectile?.projectile;
            if (props == null || projectile == null) return null;
            var burst = System.Math.Max(1, props.burstShotCount);
            var damage = (double)projectile.GetDamageAmount(weapon) * burst;
            var cycle = warmupSeconds + cooldownSeconds + (burst - 1) * props.ticksBetweenBurstShots / 60.0;
            if (cycle <= 0 || double.IsNaN(damage) || double.IsInfinity(damage)) return null;
            return damage / cycle;
        }

        // PawnRangedDps is the primary weapon's ranged damage per second with
        // the pawn's aiming delay; 0 without a ranged primary.
        internal static double PawnRangedDps(Pawn pawn)
        {
            var weapon = pawn.equipment?.Primary;
            var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (weapon == null || verb == null || verb.IsMeleeAttack) return 0;
            var warmup = verb.verbProps.warmupTime * pawn.GetStatValue(StatDefOf.AimingDelayFactor);
            return RangedDps(verb, weapon, warmup, weapon.GetStatValue(StatDefOf.RangedWeapon_Cooldown)) ?? 0;
        }

        // TurretDps is a turret gun's damage per second from its gun's verb
        // and the turret definition's burst timing (Building_TurretGun's
        // BurstCooldownTime and mean warmup); null for other buildings.
        internal static double? TurretDps(Building building)
        {
            if (!(building is Building_TurretGun turret) || turret.gun == null) return null;
            var verb = turret.AttackVerb;
            if (verb == null) return null;
            var def = turret.def.building;
            var cooldown = def.turretBurstCooldownTime >= 0 ? def.turretBurstCooldownTime : verb.verbProps.defaultCooldownTime;
            return RangedDps(verb, turret.gun, def.turretBurstWarmupTime.Average, cooldown);
        }
    }
}
