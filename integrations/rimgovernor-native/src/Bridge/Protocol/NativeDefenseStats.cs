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

        // PawnRangedHitFactor is the pawn's hit chance with its primary over
        // the reference gunner's (#3845): the factor its ranged DPS counts
        // for in defense capacity. 0 without a ranged primary: a pawn that
        // cannot shoot lands no shots.
        internal static double PawnRangedHitFactor(Pawn pawn)
        {
            var weapon = pawn.equipment?.Primary;
            var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
            if (weapon == null || verb == null || verb.IsMeleeAttack) return 0;
            var props = verb.verbProps;
            var distance = EngagementDistance(props);
            var shooter = props.canGoWild ? ShotReport.HitFactorFromShooter(pawn, distance) : 1f;
            return StandardHitChance(shooter, props.GetHitChanceFactor(weapon, distance)) / ReferenceHitChance();
        }

        // EngagementDistance is the range a shooter takes position at:
        // CastPositionFinder scores cells by their distance to the target
        // against OptimalRangeFactor x the verb's range. The verb's own range
        // stands for EffectiveRange, which only the current weather caps.
        internal static float EngagementDistance(VerbProperties props) => OptimalRangeFactor() * props.range;

        // StandardHitChance is ShotReport.AimOnTargetChance_StandardTarget
        // from its shooter and equipment factors, with weather, blind smoke,
        // execution and darkness at their neutral 1 and 0: a standing
        // human-sized target out of cover, the factors the colony's
        // defense carries into every fight.
        private static float StandardHitChance(float shooter, float equipment) =>
            System.Math.Max(shooter * equipment, ShootTuning.MinAimOnChance_StandardTarget);

        // ReferenceHitChance is the reference gunner's hit chance: the shooter
        // the raid-point scale (DefenseCapacityPolicy.DefensePointsPerDps) and
        // the exposure calibration (harm-exposure-calibration.md) were measured
        // with. That is a pawn at the game's no-skill shooting accuracy
        // (ShootingAccuracyPawn.noSkillOffset, the level-8 shooter the combat
        // lab stages) with a normal assault rifle at its engagement distance.
        // A pawn as accurate counts its ranged DPS at 1.
        internal static float ReferenceHitChance()
        {
            // No ThingDefOf field names the assault rifle; the def name is the only route.
            var rifle = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_AssaultRifle")
                ?? throw new System.InvalidOperationException("Gun_AssaultRifle, the reference gunner's weapon, is not loaded");
            var stat = StatDefOf.ShootingAccuracyPawn;
            var accuracy = stat.postProcessCurve.Evaluate(ThingDefOf.Human.statBases.GetStatValueFromList(stat, stat.defaultBaseValue) + stat.noSkillOffset);
            // A clone of the rifle's verb with its abstract accuracy stats lets
            // VerbProperties.GetHitChanceFactor band them without a made thing.
            var props = rifle.Verbs[0].MemberwiseClone();
            props.accuracyTouch = rifle.GetStatValueAbstract(StatDefOf.AccuracyTouch);
            props.accuracyShort = rifle.GetStatValueAbstract(StatDefOf.AccuracyShort);
            props.accuracyMedium = rifle.GetStatValueAbstract(StatDefOf.AccuracyMedium);
            props.accuracyLong = rifle.GetStatValueAbstract(StatDefOf.AccuracyLong);
            var distance = EngagementDistance(props);
            return StandardHitChance(ShotReport.HitFactorFromShooter(null, distance, accuracy), props.GetHitChanceFactor(null, distance));
        }

        // OptimalRangeFactor is CastPositionFinder's private constant; reflection is the only route to it.
        private static float OptimalRangeFactor() =>
            typeof(Verse.AI.CastPositionFinder).GetField("OptimalRangeFactor", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetRawConstantValue() is float factor
                ? factor : throw new System.InvalidOperationException("CastPositionFinder.OptimalRangeFactor is not a float constant in this game version");

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
