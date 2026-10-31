using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // GiveJobIntent Capture and Rescue. Both are single fixed
    // vanilla jobs (Capture, Rescue) that carry a downed patient to a bed. A
    // capture takes the prisoner bed RestUtility.FindBedFor gives; a rescue
    // carries the patient to the exact bed the brain chose (targets [patient,
    // bed], JobDriver_TakeToBed's TargetIndex.B), refused unless
    // RestUtility.IsValidBedFor passes for that patient and rescuer. Checked
    // live at apply; a pawn already carrying out the job on the patient (and,
    // for a rescue, to that bed) applies again.
    internal static class NativeCustodyOperations
    {
        // Manhunter mental state or a hostile faction. Rescue refuses a
        // hostile patient (vanilla offers only capture for one); capture
        // requires it.
        private static bool HostileToPlayer(Pawn patient)
        {
            try
            {
                var mental = patient.MentalState?.def?.defName;
                if (!string.IsNullOrEmpty(mental) && mental.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                var faction = patient.Faction;
                if (faction == null) return false;
                var player = Faction.OfPlayerSilentFail;
                return player != null && faction != player && faction.HostileTo(player);
            }
            catch { return false; }
        }

        private static bool CaptureEligible(Pawn pawn, Pawn patient) => patient != null && !patient.Dead && patient.Spawned
            && patient.Map == pawn.Map && patient.CanBeCaptured() && HealthAIUtility.CanRescueNow(pawn, patient, true)
            && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && patient.HostileTo(Faction.OfPlayerSilentFail);

        // FloatMenuOptionProvider_RescuePawn's rules, drafted or not.
        internal static bool RescueEligible(Pawn pawn, Pawn patient) => patient != null && !patient.Dead && patient.Spawned
            && patient.Map == pawn.Map && !ReferenceEquals(patient, pawn)
            && HealthAIUtility.CanRescueNow(pawn, patient, true) && !HostileToPlayer(patient);

        private static JobDef Def(JobOrderKind kind) => kind == JobOrderKind.Capture ? JobDefOf.Capture : JobDefOf.Rescue;

        private static bool Running(JobOrderKind kind, Pawn pawn, Pawn patient, Building_Bed? bed) => pawn.CurJob != null && pawn.CurJob.def == Def(kind) && pawn.CurJob.targetA.Thing == patient
            && (kind == JobOrderKind.Capture || pawn.CurJob.targetB.Thing == bed);

        // A pawn studied at a holding platform is captured to a
        // holding platform, not a prisoner bed: the game's own order
        // (StudyUtility.TargetHoldingPlatformForEntity with a carrier) sets the
        // entity's targetHolder and gives the carrier CarryToEntityHolder, the
        // job WorkGiver_TakeEntityToHoldingPlatform builds. The platform is the
        // available one with the highest containment strength the carrier can
        // reserve and reach, ties by thing id; the brain decided the rule's margin
        // against these same native strengths.
        internal static CompHoldingPlatformTarget? EntityTarget(Pawn patient) =>
            // Ordinary humans also carry this comp with Anomaly enabled. Use the
            // same classification as FloatMenuOptionProvider_CaptureEntity.
            patient.GetComp<CompHoldingPlatformTarget>() is { StudiedAtHoldingPlatform: true } target ? target : null;

        private static bool EntityRunning(Pawn pawn, Pawn patient) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.CarryToEntityHolder && pawn.CurJob.targetB.Thing == patient;

        private static Building_HoldingPlatform? FindPlatform(Pawn pawn, Pawn patient) => patient.Map.listerThings.ThingsInGroup(ThingRequestGroup.EntityHolder)
            .OfType<Building_HoldingPlatform>()
            .Where(platform => platform.GetComp<CompEntityHolder>() is CompEntityHolder holder && holder.Available
                && pawn.CanReserveAndReach(platform, PathEndMode.ClosestTouch, Danger.Deadly))
            .OrderByDescending(platform => platform.GetComp<CompEntityHolder>().ContainmentStrength).ThenBy(platform => platform.thingIDNumber)
            .FirstOrDefault();

        private static bool EntityCaptureEligible(Pawn pawn, Pawn patient, CompHoldingPlatformTarget target) => !patient.Dead && patient.Spawned
            && patient.Map == pawn.Map && target.CanBeCaptured && patient.Downed && patient.ThreatDisabled(pawn)
            && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && patient.HostileTo(Faction.OfPlayerSilentFail);

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out Pawn? patient, out Building_Bed? bed, out Building_HoldingPlatform? platform)
        {
            patient = null; bed = null; platform = null;
            if (!NativeGiveJob.TryPawn(intent, context, out pawn, out var snapshot, out var failure)) return failure;
            if (!snapshot.Eligible) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Custody requires an eligible pawn.");
            patient = GameThread.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent.TargetId);
            if (patient == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact patient pawn is not spawned on this map.");
            if (intent.Kind == JobOrderKind.Capture && EntityTarget(patient) is CompHoldingPlatformTarget entity)
            {
                if (EntityRunning(pawn, patient)) return null;
                if (!EntityCaptureEligible(pawn, patient, entity))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Capture refused: the entity must be a downed, hostile, capturable entity the pawn can carry.");
                platform = FindPlatform(pawn, patient);
                if (platform == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "No available holding platform the pawn can reach.");
                if (!pawn.CanReserveAndReach(patient, PathEndMode.ClosestTouch, Danger.Deadly))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The pawn cannot reserve and reach the entity.");
                return null;
            }
            bool capture = intent.Kind == JobOrderKind.Capture;
            if (!capture)
            {
                if (!intent.HasBedId || !GameThread.IsIdentifier(intent.BedId))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Rescue requires the exact bed.");
                bed = RefIndex.Thing<Building_Bed>(GameThread.LoadedMap(context), intent.BedId);
                if (bed == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact rescue bed is not spawned on this map.");
            }
            if (Running(intent.Kind, pawn, patient, bed)) return null;
            if (capture ? !CaptureEligible(pawn, patient) : !RescueEligible(pawn, patient))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, capture
                    ? "Capture refused: the patient must be a downed, capturable hostile the pawn can carry."
                    : "Rescue refused: the patient must be a downed, non-hostile pawn the rescuer can carry.");
            if (capture)
            {
                bed = RestUtility.FindBedFor(patient, pawn, false, false, GuestStatus.Prisoner);
                if (bed == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "No native prisoner bed is available for this worker and patient.");
            }
            else if (!RestUtility.IsValidBedFor(bed, patient, pawn, checkSocialProperness: false))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Rescue refused: the bed is not a valid bed for this patient and rescuer.");
            if (!pawn.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The pawn cannot reserve and reach the patient.");
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var patient, out var bed, out var platform);
            if (failure != null || pawn == null || patient == null) throw new InvalidOperationException(failure?.Detail);
            if (intent.Kind == JobOrderKind.Capture && EntityTarget(patient) is CompHoldingPlatformTarget entity)
            {
                if (EntityRunning(pawn, patient)) return NativeGiveJob.Evidence(pawn, patient, pawn.CurJob, false);
                entity.targetHolder = platform;
                var carry = JobMaker.MakeJob(JobDefOf.CarryToEntityHolder, platform, patient);
                carry.count = 1;
                if (!pawn.jobs.TryTakeOrderedJob(carry, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != carry.loadID)
                {
                    entity.targetHolder = null;
                    throw new InvalidOperationException("The pawn did not take the entity capture job.");
                }
                return NativeGiveJob.Evidence(pawn, patient, carry, true);
            }
            if (Running(intent.Kind, pawn, patient, bed)) return NativeGiveJob.Evidence(pawn, patient, pawn.CurJob, false);
            var job = JobMaker.MakeJob(Def(intent.Kind), patient, bed);
            job.count = 1;
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID)
                throw new InvalidOperationException("The pawn did not take the custody job.");
            return NativeGiveJob.Evidence(pawn, patient, job, true);
        }
    }
}
