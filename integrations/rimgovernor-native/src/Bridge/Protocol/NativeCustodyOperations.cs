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
    // GiveJobIntent Capture and Rescue (#939). Both are single fixed
    // vanilla jobs (Capture, Rescue) that carry a downed patient to a bed -- a
    // prisoner bed for capture, an ordinary or guest bed for rescue. Checked
    // live at apply; a pawn already carrying out the job on the patient
    // applies again.
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
                if (!string.IsNullOrEmpty(mental) && mental!.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0) return true;
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

        internal static bool FindBed(JobOrderKind kind, Pawn pawn, Pawn patient, out Building_Bed? bed)
        {
            bed = kind == JobOrderKind.Capture
                ? RestUtility.FindBedFor(patient, pawn, false, false, GuestStatus.Prisoner)
                : RestUtility.FindBedFor(patient, pawn, checkSocialProperness: false)
                    ?? RestUtility.FindBedFor(patient, pawn, false, ignoreOtherReservations: true);
            return bed != null;
        }

        private static JobDef Def(JobOrderKind kind) => kind == JobOrderKind.Capture ? JobDefOf.Capture : JobDefOf.Rescue;

        private static bool Running(JobOrderKind kind, Pawn pawn, Pawn patient) => pawn.CurJob != null && pawn.CurJob.def == Def(kind) && pawn.CurJob.targetA.Thing == patient;

        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Pawn? patient, out Building_Bed? bed)
        {
            patient = null; bed = null;
            var failure = NativeGiveJob.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (!snapshot!.Eligible) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Custody requires an eligible pawn.");
            patient = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent.TargetId);
            if (patient == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact patient pawn is not spawned on this map.");
            if (Running(intent.Kind, pawn!, patient)) return null;
            bool capture = intent.Kind == JobOrderKind.Capture;
            if (capture ? !CaptureEligible(pawn!, patient) : !RescueEligible(pawn!, patient))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, capture
                    ? "Capture refused: the patient must be a downed, capturable hostile the pawn can carry."
                    : "Rescue refused: the patient must be a downed, non-hostile pawn the rescuer can carry.");
            if (!FindBed(intent.Kind, pawn!, patient, out bed) || bed == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "No native bed is available for this worker and patient.");
            if (!pawn!.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The pawn cannot reserve and reach the patient.");
            return null;
        }

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var patient, out var bed);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(intent.Kind, pawn!, patient!)) return NativeGiveJob.Evidence(pawn!, patient!, pawn!.CurJob, false);
            var job = JobMaker.MakeJob(Def(intent.Kind), patient, bed);
            job.count = 1;
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID)
                throw new InvalidOperationException("The pawn did not take the custody job.");
            return NativeGiveJob.Evidence(pawn, patient!, job, true);
        }
    }
}
