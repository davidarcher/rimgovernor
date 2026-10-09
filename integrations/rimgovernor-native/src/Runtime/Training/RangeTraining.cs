#nullable disable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Runtime
{
    // The training job's rules (#2610): who is eligible, which skill drills, the
    // XP per cycle and the daily budget. The mod's work type, work giver and job
    // defs are Defs/WorkTypeDefs, WorkGiverDefs and JobDefs/RimGovernorTraining.xml.
    internal static class RangeTraining
    {
        public const string ShootingJob = "RimGovernor_TrainShooting";
        public const string MeleeJob = "RimGovernor_TrainMelee";
        public const string WeaponDef = "Bow_Training";

        // Mirrors go/internal/policy TrainingSkillTarget (a Go test reads this line):
        // a colonist whose best enabled combat skill reaches it stops drilling.
        public const int SkillTarget = 8;

        // XP per cycle second (verbProps.AdjustedFullCycleTime), applied by direct
        // SkillRecord.Learn. Vanilla pays 170 (shooting at a hostile pawn) / 20
        // (a friendly pawn) and 200 (melee) per cycle second but pays nothing for a
        // non-pawn target (IsTargetImmobile), so this drill is the only source. The
        // drill pays about 15% of the fight rate: a practice bow cycle is 3 s, so
        // 75 XP a shot and about 90 per melee swing (the ratio keeps vanilla's 200:170).
        public const float ShootingXpPerCycleSecond = 25f;
        public const float MeleeXpPerCycleSecond = 30f;

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

        public static float BowRange => BowDef?.Verbs.FirstOrDefault()?.range ?? 0f;

        public static float CycleXp(bool shooting, float cycleSeconds) => (shooting ? ShootingXpPerCycleSecond : MeleeXpPerCycleSecond) * cycleSeconds;

        public static float SpentToday(Pawn pawn)
        {
            return Spent.TryGetValue(pawn.thingIDNumber, out var row) && row.Key == GenDate.DaysPassed ? row.Value : 0f;
        }

        public static float Remaining(Pawn pawn) => System.Math.Max(0f, DailyXpBudget - SpentToday(pawn));

        public static void Spend(Pawn pawn, float applied)
        {
            Spent[pawn.thingIDNumber] = new KeyValuePair<int, float>(GenDate.DaysPassed, SpentToday(pawn) + applied);
        }

        // Pure: the best level among the enabled combat skills; -1 when none is enabled.
        public static int BestEnabledLevel(int melee, bool meleeEnabled, int shooting, bool shootingEnabled)
        {
            var best = -1;
            if (meleeEnabled) best = System.Math.Max(best, melee);
            if (shootingEnabled) best = System.Math.Max(best, shooting);
            return best;
        }

        public static bool BelowTarget(Pawn pawn)
        {
            var skills = pawn.skills;
            if (skills == null) return false;
            var melee = skills.GetSkill(SkillDefOf.Melee);
            var shooting = skills.GetSkill(SkillDefOf.Shooting);
            var best = BestEnabledLevel(melee.Level, !melee.TotallyDisabled, shooting.Level, !shooting.TotallyDisabled);
            return best >= 0 && best < SkillTarget;
        }

        public static bool Eligible(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && pawn.IsColonist && !pawn.Downed && !pawn.Drafted
                && pawn.DevelopmentalStage == DevelopmentalStage.Adult && pawn.equipment != null && pawn.inventory != null
                && BelowTarget(pawn) && Remaining(pawn) > 0f;
        }

        // Which skill drills: the higher usable level (it reaches the target soonest),
        // then the stronger passion, then shooting (the safer drill). Shooting is
        // unusable for a Brawler; melee for a pawn whose Melee is disabled.
        public static bool TryChoose(Pawn pawn, out bool shooting)
        {
            shooting = false;
            var melee = pawn.skills.GetSkill(SkillDefOf.Melee);
            var shoot = pawn.skills.GetSkill(SkillDefOf.Shooting);
            var meleeOk = !melee.TotallyDisabled;
            var shootOk = !shoot.TotallyDisabled && !(pawn.story?.traits?.HasTrait(TraitDefOf.Brawler) ?? false);
            if (!meleeOk && !shootOk) return false;
            if (meleeOk && !shootOk) return true;
            if (shootOk && !meleeOk) { shooting = true; return true; }
            if (shoot.Level != melee.Level) shooting = shoot.Level > melee.Level;
            else shooting = (int)shoot.passion >= (int)melee.passion;
            return true;
        }

        public static IEnumerable<Thing> Stands(Map map)
        {
            var def = StandDef;
            return def == null || map == null ? Enumerable.Empty<Thing>() : map.listerThings.ThingsOfDef(def);
        }

        // The dummy a stand faces: the nearest on the stand's row or column, in the
        // same room, within the bow's range and in clear sight.
        public static Thing DummyFor(Thing stand)
        {
            var dummyDef = DummyDef;
            var map = stand.Map;
            if (dummyDef == null || map == null) return null;
            var range = BowRange;
            var room = stand.Position.GetRoom(map);
            Thing best = null;
            var bestDistance = float.MaxValue;
            foreach (var dummy in map.listerThings.ThingsOfDef(dummyDef))
            {
                var d = dummy.Position - stand.Position;
                if ((d.x != 0 && d.z != 0) || (d.x == 0 && d.z == 0)) continue;
                var distance = System.Math.Abs(d.x + d.z);
                if (distance >= bestDistance || distance > range || dummy.HitPoints <= 0) continue;
                if (dummy.Position.GetRoom(map) != room || !GenSight.LineOfSight(stand.Position, dummy.Position, map)) continue;
                best = dummy;
                bestDistance = distance;
            }
            return best;
        }

        // The lane cell a melee pawn strikes from: the one before the dummy, on the
        // stand's side.
        public static IntVec3 StrikeCell(Thing stand, Thing dummy)
        {
            var d = dummy.Position - stand.Position;
            var step = new IntVec3(System.Math.Sign(d.x), 0, System.Math.Sign(d.z));
            return dummy.Position - step;
        }
    }
}
