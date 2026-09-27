#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // PawnOrderIntent kind TEND (#939), the doctor dispatch of
    // MaintainMedicalCare and CriticalMedical. First WorkGiver_Tend's own
    // JobOnThing, which is what "Prioritize tending X" issues for a patient
    // in a bed and chooses the medicine; when that yields nothing and the
    // patient is down on the ground, the job the drafted float menu builds
    // (JobDefOf.TendPatient with draftedTend, the best inventory medicine or
    // none). The Work-tab priority is not consulted -- only a disabled Doctor
    // work type or a missing capacity refuses. Checked live at apply; a
    // doctor already tending the patient applies again.
    internal static class NativeTendOperations
    {
        private static bool Running(Pawn pawn, Pawn patient) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.TendPatient && pawn.CurJob.targetA.Thing == patient;

        private static WorkGiver_Tend? Giver() => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.giverClass != null && typeof(WorkGiver_Tend).IsAssignableFrom(d.giverClass))
            .Select(d => d.Worker as WorkGiver_Tend).FirstOrDefault(w => w != null);

        // Resolve names the first gate the order fails and builds the job it
        // would issue.
        private static Common.Failure? Resolve(Operations.PawnOrderIntent intent, Common.ObservationContext context, out Pawn? pawn, out Pawn? patient, out Job? job)
        {
            patient = null; job = null;
            var failure = NativePawnOrderIntent.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (!snapshot!.Eligible) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Tend requires an eligible doctor.");
            var map = ProtoBoundary.LoadedMap(context);
            patient = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == intent.TargetId);
            if (patient == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact patient pawn is not spawned on this map.");
            if (patient.Dead) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is dead and cannot be tended.");
            if (Running(pawn!, patient)) return null;
            if (!patient.health.HasHediffsNeedingTend())
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient has nothing that needs tending right now.");
            if (patient.InAggroMentalState)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is in an aggressive mental state and cannot be tended until it ends.");
            if (pawn!.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The doctor has the Doctor work type disabled.");
            var giver = Giver();
            if (giver == null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "WorkGiver_Tend is unavailable in this game.");
            var missing = giver.MissingRequiredCapacity(pawn);
            if (missing != null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The doctor is missing a capacity tending needs (" + missing.defName + ").");
            if (!pawn.CanReach(patient, PathEndMode.ClosestTouch, Danger.Deadly))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The doctor cannot reach the patient.");
            // The ordinary path: what "Prioritize tending X" issues, medicine
            // and bed chosen by the work giver. It refuses a patient on the
            // ground, which is the drafted float menu's job instead.
            if (giver.HasJobOnThing(pawn, patient, true)) job = giver.JobOnThing(pawn, patient, true);
            if (job == null)
            {
                if (patient.InBed())
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is in a bed and WorkGiver_Tend makes no job for this doctor (already being tended, or the bed is reserved).");
                if (!patient.Downed)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The patient is up and not in a bed; WorkGiver_Tend makes no job and ground tending needs a downed patient.");
                var medicine = HealthAIUtility.FindBestMedicine(pawn, patient, onlyUseInventory: true);
                job = medicine == null ? JobMaker.MakeJob(JobDefOf.TendPatient, patient) : JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
                job.count = 1;
                job.draftedTend = true;
            }
            if (job.def != JobDefOf.TendPatient)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "WorkGiver_Tend produced a " + job.def.defName + " job rather than TendPatient.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.PawnOrderIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.PawnOrderIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var patient, out var job);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, patient!)) return NativePawnOrderIntent.Evidence(pawn!, patient!, pawn!.CurJob, false);
            if (!pawn!.jobs.TryTakeOrderedJob(job!, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job!.loadID)
                throw new InvalidOperationException("The doctor did not take the tend job.");
            return NativePawnOrderIntent.Evidence(pawn, patient!, job, true);
        }
    }
}
