#nullable disable
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Runtime
{
    // A pawn's part in a sparring bout (#2708). TargetA is its marker. Walk to
    // the marker, tell the registry it arrived, and hold there until the bout
    // ends. This is the minimal entry that makes formation testable; the gear
    // swap, the swings and the stop rule are #2709's.
    //
    // The bout lives only in memory: a job restored from a save finds no bout
    // and ends Incompletable.
    public sealed class JobDriver_Spar : JobDriver
    {
        private const TargetIndex MarkerIndex = TargetIndex.A;

        // Not saved: after a reload it is false, so a vanished bout is a failure.
        private bool sawFight;

        private SparringBouts Bouts => SparringBouts.For(pawn.Map);

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(MarkerIndex), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(MarkerIndex);
            this.FailOn(() => !sawFight && Bouts.BoutOf(pawn) == null);
            AddFinishAction(delegate { Bouts.Leave(pawn); });
            yield return Toils_Goto.GotoThing(MarkerIndex, PathEndMode.OnCell);
            var arrive = ToilMaker.MakeToil("ArriveAtMarker");
            arrive.initAction = delegate { Bouts.Arrive(pawn); };
            yield return arrive;
            var hold = ToilMaker.MakeToil("HoldMarker");
            hold.defaultCompleteMode = ToilCompleteMode.Never;
            hold.initAction = delegate { pawn.pather.StopDead(); };
            hold.tickAction = delegate
            {
                Bouts.Upkeep();
                var bout = Bouts.BoutOf(pawn);
                if (bout != null && bout.State == BoutState.Fighting) sawFight = true;
                else if (bout == null && sawFight) EndJobWith(JobCondition.Succeeded);
            };
            yield return hold;
        }
    }
}
