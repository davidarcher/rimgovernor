#nullable disable
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Runtime
{
    // One drill session at a range stand (#2610, ranged only since #2686). TargetA
    // is the stand, TargetB the dummy in its column; job.maxNumStaticAttacks bounds
    // the cycles.
    //
    // Walk onto the stand, swap the pawn's weapon for the best unlocked practice
    // weapon (the real one moves to the pawn's inventory), then fire its real verb
    // at the dummy. The range is open air: the verb never hits a pawn in the way
    // (TryStartCastOn with friendly fire prevented), and a wild shot flies on.
    //
    // Vanilla pays no verb XP for a non-pawn target, so each completed shot is
    // paid by direct SkillRecord.Learn at the weapon's xpPerShot, within the pawn's
    // daily budget. The shooter mends the dummy itself (Mend): before a cycle when
    // the next hit could destroy it, and at the end of the session. The finish action runs on every end of the job (completion,
    // interruption, drafting, the pawn's death, which despawns it before its
    // belongings drop) and puts the real weapon back and destroys the practice weapon.
    public sealed class JobDriver_TrainRange : JobDriver
    {
        private const TargetIndex StandIndex = TargetIndex.A;
        private const TargetIndex DummyIndex = TargetIndex.B;

        private Thing realWeapon;
        private ThingWithComps trainingBow;
        private int cyclesDone;
        private int castStartedTick = -1;
        private bool mending;
        private float repairCredit;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref mending, "mending", false);
            Scribe_Values.Look(ref repairCredit, "repairCredit", 0f);
            Scribe_References.Look(ref realWeapon, "realWeapon");
            Scribe_References.Look(ref trainingBow, "trainingBow");
            Scribe_Values.Look(ref cyclesDone, "cyclesDone", 0);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(StandIndex), job, 1, -1, null, errorOnFailed)
                && pawn.Reserve(job.GetTarget(DummyIndex), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(StandIndex);
            this.FailOnDespawnedOrNull(DummyIndex);
            this.FailOn(() => !RangeTraining.BelowTarget(pawn));
            AddFinishAction(delegate { RestoreWeapon(); });
            yield return Toils_Goto.GotoThing(StandIndex, PathEndMode.OnCell);
            var swap = ToilMaker.MakeToil("SwapToTrainingWeapon");
            swap.initAction = SwapToBow;
            yield return swap;
            var drill = ToilMaker.MakeToil("Drill");
            drill.defaultCompleteMode = ToilCompleteMode.Never;
            drill.initAction = delegate { pawn.pather.StopDead(); };
            drill.tickAction = DrillTick;
            drill.activeSkill = () => SkillDefOf.Shooting;
            yield return drill;
        }

        private void SwapToBow()
        {
            var equipment = pawn.equipment;
            var bowDef = RangeTraining.UnlockedWeapon();
            if (bowDef == null || trainingBow != null && equipment.Contains(trainingBow))
            {
                if (bowDef == null) EndJobWith(JobCondition.Incompletable);
                return;
            }
            var primary = equipment.Primary;
            if (primary != null)
            {
                if (!equipment.TryTransferEquipmentToContainer(primary, pawn.inventory.innerContainer))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                realWeapon = primary;
            }
            trainingBow = (ThingWithComps)ThingMaker.MakeThing(bowDef);
            equipment.AddEquipment(trainingBow);
        }

        private void DrillTick()
        {
            var dummy = job.GetTarget(DummyIndex).Thing;
            var stances = pawn.stances;
            if (dummy == null || dummy.Destroyed)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            if (castStartedTick >= 0)
            {
                // The shot lands when the warmup ends; a cast cut short never fires.
                var verb = pawn.equipment.PrimaryEq?.PrimaryVerb;
                if (verb != null && verb.LastShotTick >= castStartedTick)
                {
                    castStartedTick = -1;
                    Pay();
                }
                else if (!stances.FullBodyBusy)
                {
                    castStartedTick = -1;
                }
                return;
            }
            if (stances.FullBodyBusy) return;
            var done = cyclesDone >= job.maxNumStaticAttacks || RangeTraining.Remaining(pawn) <= 0f;
            if (mending || done && dummy.HitPoints < dummy.MaxHitPoints
                || RangeTraining.NeedsMend(dummy.HitPoints, RangeTraining.HitDamage(trainingBow?.def)))
            {
                mending = true;
                Mend(dummy);
                return;
            }
            if (done)
            {
                EndJobWith(JobCondition.Succeeded);
                return;
            }
            StartShot(dummy);
        }

        // The shooter repairs the dummy at JobDriver_Repair's rate (one hit point per
        // 20 / (ConstructionSpeed x 1.7) ticks) for the same small Construction XP;
        // no materials, no Construction work type, no home area. Until it is whole.
        private void Mend(Thing dummy)
        {
            pawn.skills?.Learn(SkillDefOf.Construction, 0.05f);
            repairCredit -= pawn.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f;
            if (repairCredit <= 0f)
            {
                repairCredit += 20f;
                dummy.HitPoints = System.Math.Min(dummy.HitPoints + 1, dummy.MaxHitPoints);
            }
            if (dummy.HitPoints >= dummy.MaxHitPoints) mending = false;
        }

        private void StartShot(Thing dummy)
        {
            var verb = pawn.equipment.PrimaryEq?.PrimaryVerb;
            if (trainingBow == null || verb == null || verb.EquipmentSource != trainingBow || !verb.CanHitTarget(dummy))
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            // No stray pawn is hit and no friendly fire.
            if (verb.TryStartCastOn(dummy, false, false, true)) castStartedTick = Find.TickManager.TicksGame;
            else EndJobWith(JobCondition.Incompletable);
        }

        // One completed shot: direct Learn, bounded by what is left of the day's budget.
        private void Pay()
        {
            cyclesDone++;
            var record = pawn.skills.GetSkill(SkillDefOf.Shooting);
            var factor = record.LearnRateFactor(true);
            var xp = RangeTraining.ShotXp(trainingBow?.def);
            var applied = xp * factor;
            var remaining = RangeTraining.Remaining(pawn);
            if (applied > remaining)
            {
                xp = factor > 0f ? remaining / factor : 0f;
                applied = remaining;
            }
            if (xp <= 0f) return;
            record.Learn(xp, true);
            RangeTraining.Spend(pawn, applied);
        }

        // Idempotent: puts the real weapon back in hand and destroys the bow, whatever ended the job.
        private void RestoreWeapon()
        {
            if (trainingBow != null)
            {
                if (pawn.equipment != null && pawn.equipment.Contains(trainingBow)) pawn.equipment.Remove(trainingBow);
                if (!trainingBow.Destroyed) trainingBow.Destroy();
                trainingBow = null;
            }
            if (realWeapon != null)
            {
                var inventory = pawn.inventory?.innerContainer;
                if (realWeapon is ThingWithComps weapon && inventory != null && inventory.Contains(weapon) && pawn.equipment != null && pawn.equipment.Primary == null)
                {
                    inventory.Remove(weapon);
                    pawn.equipment.AddEquipment(weapon);
                }
                realWeapon = null;
            }
        }
    }
}
