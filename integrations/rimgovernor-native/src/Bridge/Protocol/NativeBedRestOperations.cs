using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // GiveJobIntent LayDown: a walking patient the player controls lies down in
    // the exact bed the brain chose (JobDriver_LayDown's TargetIndex.A is the
    // bed; the pawn is the patient itself). Refused unless the patient is an
    // undrafted, standing colonist the player controls and
    // RestUtility.IsValidBedFor passes for it walking there. Checked live at
    // apply; a patient already lying down in that bed, or walking there, applies
    // again.
    internal static class NativeBedRestOperations
    {
        private static bool Running(Pawn pawn, Building_Bed bed) => pawn.CurrentBed() == bed
            || pawn.CurJob != null && pawn.CurJob.def == JobDefOf.LayDown && pawn.CurJob.targetA.Thing == bed;

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out Building_Bed? bed)
        {
            bed = null;
            if (!NativeGiveJob.TryPawn(intent, context, out pawn, out _, out var failure)) return failure;
            bed = RefIndex.Thing<Building_Bed>(GameThread.LoadedMap(context), intent.TargetId);
            if (bed == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact bed is not spawned on this map.");
            if (Running(pawn, bed)) return null;
            string? reason = null;
            if (pawn.Dead || !pawn.IsColonistPlayerControlled) reason = "the patient is not a colonist the player controls";
            else if (pawn.Drafted) reason = "the patient is drafted";
            else if (pawn.Downed) reason = "the patient is downed and must be carried";
            else if (pawn.InMentalState) reason = "the patient is in a mental state";
            else if (!RestUtility.IsValidBedFor(bed, pawn, pawn, checkSocialProperness: false)) reason = "the bed is not a valid bed for the patient";
            return reason == null ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Lay down refused: " + reason + ".");
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var bed);
            if (failure != null || pawn == null || bed == null) throw new InvalidOperationException(failure?.Detail);
            if (Running(pawn, bed)) return NativeGiveJob.Evidence(pawn, bed, pawn.CurJob ?? JobMaker.MakeJob(JobDefOf.LayDown, bed), false);
            var job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID)
                throw new InvalidOperationException("The patient did not take the lay down job.");
            return NativeGiveJob.Evidence(pawn, bed, job, true);
        }
    }
}
