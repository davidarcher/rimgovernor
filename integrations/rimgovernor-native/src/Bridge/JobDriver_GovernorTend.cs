using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
namespace HomeBridge.BridgeTools;

/// <summary>One exact native treatment batch. No medicine search, duplicate pickup or repeat treatment.</summary>
public sealed class JobDriver_GovernorTend : JobDriver
{
    public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed)
        && (!job.targetB.HasThing || pawn.Reserve(job.targetB, job, 1, 1, null, errorOnFailed));
    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
        this.FailOn(() => job.targetA.Pawn == null || !HealthAIUtility.ShouldBeTendedNowByPlayer(job.targetA.Pawn)
            || !WorkGiver_Tend.GoodLayingStatusForTend(job.targetA.Pawn, pawn)
            || pawn == job.targetA.Pawn && pawn.playerSettings?.selfTend != true
            || pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
        var patient = job.targetA.Pawn;
        if (job.targetB.HasThing)
        {
            this.FailOn(() => job.targetB.Thing == null || job.targetB.Thing.Destroyed || job.targetB.Thing.IsForbidden(pawn)
                || patient.playerSettings?.medCare.AllowsMedicine(job.targetB.Thing.def) != true);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, failIfStackCountLessThanJobCount: true);
        }
        yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
        var wait = Toils_General.Wait((int)(600f / pawn.GetStatValue(StatDefOf.MedicalTendSpeed)));
        wait.FailOn(() => !pawn.CanReachImmediate(patient, PathEndMode.Touch));
        wait.WithProgressBarToilDelay(TargetIndex.A); wait.activeSkill = () => SkillDefOf.Medicine;
        yield return wait;
        yield return Toils_Tend.FinalizeTend(patient);
    }
}

