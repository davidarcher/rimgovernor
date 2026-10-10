#nullable disable
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Runtime
{
    // A pawn's part in a sparring bout (#2709). TargetA is its marker. Walk to the
    // marker, tell the registry it arrived and wait for the bout to gather; then
    // swap into the tier's practice gear and fight the opponent the bout picks
    // through the practice weapon's real melee verb, until the stop rule (pain,
    // bleeding, exchange count) or the end of the bout.
    //
    // Swap: the real weapon moves to the pawn's inventory and the practice weapon
    // takes its hand; every unlocked worn apparel piece moves to the inventory
    // (still the pawn's, still counted toward its mass) and the tier's practice set
    // is worn in its place. Vanilla resolves the hits, pain, injuries and healing
    // and pays the melee XP (Verb_MeleeAttack.TryCastShot): no direct Learn.
    //
    // The finish action runs on every end of the job (completion, interruption,
    // drafting, downing, the pawn's death, which despawns it before its belongings
    // drop) and undoes the swap; it is idempotent, as TrainRange's RestoreWeapon.
    //
    // meleeThreat: Verb_MeleeAttack sets the struck pawn's mindState.meleeThreat,
    // and the only reader (JobGiver_ReactToCloseMeleeThreat) is a think-tree node
    // consulted when a job ends and, mid-job, from Pawn_JobTracker.Notify_DamageTaken
    // unless the job def's checkOverrideOnDamage is Never. The spar JobDef says
    // Never, so a hit never makes the struck pawn fight back with its real gear,
    // flee or drop the job; the finish action clears the threat left by a partner,
    // so none is acted on once the job is over.
    //
    // The bout lives only in memory: a job restored from a save finds no bout and
    // ends Incompletable, whose finish action restores the saved gear.
    public sealed class JobDriver_Spar : JobDriver
    {
        private const TargetIndex MarkerIndex = TargetIndex.A;

        private ThingWithComps realWeapon;
        private ThingWithComps practiceWeapon;
        private List<Apparel> setAside = new List<Apparel>();
        private List<Apparel> practiceApparel = new List<Apparel>();
        private int exchanges;

        // Not saved: after a reload they are empty, so a vanished bout is a failure.
        private bool sawFight;
        private SparringStop stop;
        private readonly List<Pawn> partners = new List<Pawn>();

        private SparringBouts Bouts => SparringBouts.For(pawn.Map);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref realWeapon, "realWeapon");
            Scribe_References.Look(ref practiceWeapon, "practiceWeapon");
            Scribe_Collections.Look(ref setAside, "setAside", LookMode.Reference);
            Scribe_Collections.Look(ref practiceApparel, "practiceApparel", LookMode.Reference);
            Scribe_Values.Look(ref exchanges, "exchanges", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                setAside = (setAside ?? new List<Apparel>()).Where(a => a != null).ToList();
                practiceApparel = (practiceApparel ?? new List<Apparel>()).Where(a => a != null).ToList();
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(MarkerIndex), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(MarkerIndex);
            this.FailOn(() => !sawFight && Bouts.BoutOf(pawn) == null);
            AddFinishAction(Finish);
            yield return Toils_Goto.GotoThing(MarkerIndex, PathEndMode.OnCell);
            var arrive = ToilMaker.MakeToil("ArriveAtMarker");
            arrive.initAction = delegate { Bouts.Arrive(pawn); };
            yield return arrive;
            var gather = ToilMaker.MakeToil("Gather");
            gather.defaultCompleteMode = ToilCompleteMode.Never;
            gather.initAction = delegate { pawn.pather.StopDead(); };
            gather.tickAction = delegate
            {
                Bouts.Upkeep();
                var bout = Bouts.BoutOf(pawn);
                if (bout != null && bout.State == BoutState.Fighting) ReadyForNextToil();
            };
            yield return gather;
            var swap = ToilMaker.MakeToil("SwapToPracticeGear");
            swap.initAction = delegate
            {
                sawFight = true;
                partners.AddRange(Bouts.BoutOf(pawn).Slots.Select(s => s.Pawn).Where(p => p != pawn));
                Swap();
            };
            yield return swap;
            var fight = ToilMaker.MakeToil("Fight");
            fight.defaultCompleteMode = ToilCompleteMode.Never;
            fight.tickAction = FightTick;
            yield return fight;
        }

        public int Exchanges => exchanges;

        private void FightTick()
        {
            Bouts.Upkeep();
            if (Bouts.BoutOf(pawn) == null || !SparringBouts.CanTakePart(pawn))
            {
                EndJobWith(JobCondition.Succeeded);
                return;
            }
            var health = pawn.health.hediffSet;
            stop = SparringStopRule.Check(health.PainTotal, health.BleedRateTotal, exchanges);
            if (stop != SparringStop.None)
            {
                EndJobWith(JobCondition.Succeeded);
                return;
            }
            if (pawn.stances.FullBodyBusy) return;
            var opponent = Bouts.OpponentOf(pawn);
            var verb = pawn.equipment.PrimaryEq?.PrimaryVerb;
            if (opponent == null || verb == null || verb.EquipmentSource != practiceWeapon)
            {
                EndJobWith(opponent == null ? JobCondition.Succeeded : JobCondition.Incompletable);
                return;
            }
            if (!verb.CanHitTarget(opponent))
            {
                if (!pawn.pather.Moving || pawn.pather.Destination.Thing != opponent) pawn.pather.StartPath(opponent, PathEndMode.Touch);
                return;
            }
            pawn.pather.StopDead();
            if (pawn.meleeVerbs.TryMeleeAttack(opponent, verb) && pawn.stances.FullBodyBusy) exchanges++;
        }

        private void Swap()
        {
            var weaponDef = SparringRules.UnlockedWeapon();
            var tier = weaponDef?.GetModExtension<SparringTier>();
            var apparelDefs = tier?.apparel.Select(name => DefDatabase<ThingDef>.GetNamedSilentFail(name)).ToList();
            if (weaponDef == null || apparelDefs.Any(d => d == null) || practiceWeapon != null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            var equipment = pawn.equipment;
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
            practiceWeapon = (ThingWithComps)ThingMaker.MakeThing(weaponDef);
            equipment.AddEquipment(practiceWeapon);
            foreach (var worn in pawn.apparel.WornApparel.Where(a => !pawn.apparel.IsLocked(a)).ToList())
            {
                if (pawn.apparel.TryMoveToInventory(worn)) setAside.Add(worn);
            }
            foreach (var def in apparelDefs)
            {
                var piece = (Apparel)ThingMaker.MakeThing(def);
                if (pawn.apparel.WornApparel.Any(w => !ApparelUtility.CanWearTogether(def, w.def, pawn.RaceProps.body)))
                {
                    piece.Destroy();
                    continue;
                }
                pawn.apparel.Wear(piece, false);
                practiceApparel.Add(piece);
            }
        }

        // Idempotent: whatever ended the job, the practice items are destroyed and
        // the pawn's own weapon and apparel are back where they were.
        private void Finish(JobCondition condition)
        {
            var swapped = practiceWeapon != null;
            Restore();
            ClearThreats();
            TrainingCompany.Finished(pawn, swapped && condition == JobCondition.Succeeded, partners.Count > 0);
            var map = pawn.MapHeld;
            if (map == null) return;
            var bouts = SparringBouts.For(map);
            bouts.Record(pawn, new SparringSession
            {
                EndedTick = Find.TickManager.TicksGame, Exchanges = exchanges, Stop = stop, Swapped = swapped, Condition = condition,
            });
            bouts.Leave(pawn);
        }

        private void Restore()
        {
            foreach (var piece in practiceApparel)
            {
                if (pawn.apparel != null && pawn.apparel.Contains(piece)) pawn.apparel.Remove(piece);
                if (!piece.Destroyed) piece.Destroy();
            }
            practiceApparel.Clear();
            if (practiceWeapon != null)
            {
                if (pawn.equipment != null && pawn.equipment.Contains(practiceWeapon)) pawn.equipment.Remove(practiceWeapon);
                if (!practiceWeapon.Destroyed) practiceWeapon.Destroy();
                practiceWeapon = null;
            }
            var inventory = pawn.inventory?.innerContainer;
            foreach (var worn in setAside)
            {
                if (!worn.Destroyed && inventory != null && inventory.Contains(worn) && pawn.apparel != null) pawn.apparel.Wear(worn, false);
            }
            setAside.Clear();
            if (realWeapon != null)
            {
                if (inventory != null && inventory.Contains(realWeapon) && pawn.equipment != null && pawn.equipment.Primary == null)
                {
                    inventory.Remove(realWeapon);
                    pawn.equipment.AddEquipment(realWeapon);
                }
                realWeapon = null;
            }
        }

        // A partner's last blow left this pawn a meleeThreat and this pawn's blows
        // left its partners one; none may start an attack job once the sparring is over.
        private void ClearThreats()
        {
            var mind = pawn.mindState;
            if (mind != null && partners.Contains(mind.meleeThreat)) mind.meleeThreat = null;
            foreach (var partner in partners)
            {
                if (partner.mindState != null && partner.mindState.meleeThreat == pawn) partner.mindState.meleeThreat = null;
            }
        }
    }
}
