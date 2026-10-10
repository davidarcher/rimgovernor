#nullable disable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Runtime
{
    // The range buildings (Defs/ThingDefs/TrainingRange.xml); mirrors go/internal/policy RangeDefNames.
    public static class TrainingRangeDefs
    {
        public const string Stand = "RimGovernor_TrainingBowStand";
        public const string Dummy = "RimGovernor_TrainingDummy";
    }

    // The training job's rules (#2610, ranged only since #2686): who is eligible,
    // the weapon tier, the XP per shot and the daily budget. The mod's work type,
    // work giver and job defs are Defs/WorkTypeDefs, WorkGiverDefs and
    // JobDefs/RimGovernorTraining.xml.
    internal static class RangeTraining
    {
        public const string ShootingJob = "RimGovernor_TrainShooting";
        public const string WeaponDef = "Bow_Training";

        // Vanilla pays 170 XP per cycle second shooting at a hostile pawn but
        // nothing for a non-pawn target (IsTargetImmobile), so this drill is the
        // only source: a shot pays the practice weapon's TrainingTier.xpPerShot,
        // applied by direct SkillRecord.Learn.

        // Direct Learn skips the 4,000 XP/day soft cap (it neither counts toward
        // xpSinceMidnight nor feels the 0.2 saturation factor), so the drill bounds
        // itself: a pawn takes at most this much applied XP (after the passion factor)
        // a day, leaving a quarter of the cap's headroom to real work and fights,
        // whose own XP still counts against the cap as in vanilla.
        public const float DailyXpBudget = 3000f;

        // Cycles one job runs before it ends and the work giver offers the next.
        public const int SessionCycles = 10;

        private static readonly Dictionary<int, KeyValuePair<int, float>> Spent = new Dictionary<int, KeyValuePair<int, float>>();

        public static ThingDef StandDef => DefDatabase<ThingDef>.GetNamedSilentFail(TrainingRangeDefs.Stand);
        public static ThingDef DummyDef => DefDatabase<ThingDef>.GetNamedSilentFail(TrainingRangeDefs.Dummy);
        public static ThingDef BowDef => DefDatabase<ThingDef>.GetNamedSilentFail(WeaponDef);

        // The unlocked weapon's range: the farthest a dummy may stand.
        public static float WeaponRange => UnlockedWeapon()?.Verbs.FirstOrDefault()?.range ?? 0f;

        // Practice weapons by rung, from their TrainingTier extensions (the defs are
        // fixed once loaded).
        private static List<ThingDef> tierWeapons;

        private static List<ThingDef> TierWeapons
        {
            get
            {
                if (tierWeapons == null)
                {
                    tierWeapons = DefDatabase<ThingDef>.AllDefs.Where(d => d.GetModExtension<TrainingTier>() != null)
                        .OrderBy(d => d.GetModExtension<TrainingTier>().tier).ToList();
                }
                return tierWeapons;
            }
        }

        // The gate research is a name so an Odyssey project resolves without a
        // reference; a project the game lacks never unlocks (as Go's census).
        private static bool Unlocked(TrainingTier tier)
        {
            if (string.IsNullOrEmpty(tier.gateResearch)) return true;
            var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(tier.gateResearch);
            return project != null && project.IsFinished;
        }

        // The best tier whose gate research is finished (UnlockedTrainingTier in
        // go/internal/policy/training_tier.go): the weapon, XP and ceiling.
        public static ThingDef UnlockedWeapon()
        {
            ThingDef best = null;
            foreach (var weapon in TierWeapons)
            {
                if (Unlocked(weapon.GetModExtension<TrainingTier>())) best = weapon;
            }
            return best ?? BowDef;
        }

        public static int Ceiling => UnlockedWeapon()?.GetModExtension<TrainingTier>()?.skillCeiling ?? 0;

        // A shot pays its weapon's xpPerShot.
        public static float ShotXp(ThingDef weapon) => weapon?.GetModExtension<TrainingTier>()?.xpPerShot ?? 0f;

        public static float SpentToday(Pawn pawn)
        {
            return Spent.TryGetValue(pawn.thingIDNumber, out var row) && row.Key == GenDate.DaysPassed ? row.Value : 0f;
        }

        public static float Remaining(Pawn pawn) => System.Math.Max(0f, DailyXpBudget - SpentToday(pawn));

        public static void Spend(Pawn pawn, float applied)
        {
            Spent[pawn.thingIDNumber] = new KeyValuePair<int, float>(GenDate.DaysPassed, SpentToday(pawn) + applied);
        }

        // Only Shooting trains; the drill is closed to a pawn who cannot shoot (a
        // Brawler, or Shooting disabled) or whose Shooting has reached the ceiling.
        public static bool CanShoot(Pawn pawn)
        {
            var record = pawn.skills?.GetSkill(SkillDefOf.Shooting);
            return record != null && !record.TotallyDisabled && !(pawn.story?.traits?.HasTrait(TraitDefOf.Brawler) ?? false);
        }

        public static bool BelowTarget(Pawn pawn) => CanShoot(pawn) && pawn.skills.GetSkill(SkillDefOf.Shooting).Level < Ceiling;

        public static bool Eligible(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && pawn.IsColonist && !pawn.Downed && !pawn.Drafted
                && pawn.DevelopmentalStage == DevelopmentalStage.Adult && pawn.equipment != null && pawn.inventory != null
                && BelowTarget(pawn) && Remaining(pawn) > 0f;
        }

        public static IEnumerable<Thing> Stands(Map map)
        {
            var def = StandDef;
            return def == null || map == null ? Enumerable.Empty<Thing>() : map.listerThings.ThingsOfDef(def);
        }

        // The dummy a stand faces: the nearest one sharing its column (x or z; the
        // rows are one cell apart and the planner puts the facing dummy straight
        // across), within the unlocked weapon's range. No room, no walls: the range
        // is open air, and Verb.CanHitTarget checks the shot itself.
        public static Thing DummyFor(Thing stand)
        {
            var dummyDef = DummyDef;
            var map = stand.Map;
            if (dummyDef == null || map == null) return null;
            var range = WeaponRange;
            Thing best = null;
            var bestDistance = float.MaxValue;
            foreach (var dummy in map.listerThings.ThingsOfDef(dummyDef))
            {
                var d = dummy.Position - stand.Position;
                if ((d.x != 0 && d.z != 0) || (d.x == 0 && d.z == 0)) continue;
                var distance = System.Math.Abs(d.x + d.z);
                if (distance >= bestDistance || distance > range || dummy.HitPoints <= 0) continue;
                best = dummy;
                bestDistance = distance;
            }
            return best;
        }
    }
}
