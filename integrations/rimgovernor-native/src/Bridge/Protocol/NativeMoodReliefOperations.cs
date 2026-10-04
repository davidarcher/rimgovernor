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
    // GiveJobIntent options.relieve_need (#939, #1352), EnsureMood-* relief.
    // Never changes needs, thoughts, timetables, restrictions, traits,
    // ideology or mental states; only offers one ordinary native food, rest
    // or recreation job to an undrafted colonist, checked live at apply. A
    // pawn already running a job its need giver issues applies again.
    internal sealed class Recreation : JobGiver_GetJoy
    {
        protected override bool JoyGiverAllowed(JoyGiverDef def) => !(def.Worker is JoyGiver_Ingest);
    }

    internal static class NativeMoodReliefOperations
    {
        internal static float? NeedLevel(Pawn p, Operations.Need need) => need switch
        {
            Operations.Need.Food => p.needs?.food?.CurLevelPercentage,
            Operations.Need.Rest => p.needs?.rest?.CurLevelPercentage,
            Operations.Need.Joy => p.needs?.joy?.CurLevelPercentage,
            _ => null,
        };

        private static ThinkNode_JobGiver? Giver(Operations.Need need) => need switch
        {
            Operations.Need.Food => new JobGiver_GetFood(),
            Operations.Need.Rest => new JobGiver_GetRest(),
            Operations.Need.Joy => new Recreation(),
            _ => null,
        };

        // Running: the pawn's current job already relieves this need.
        private static bool Running(Pawn pawn, Operations.Need need)
        {
            var job = pawn.CurJob;
            if (job == null) return false;
            return need switch
            {
                Operations.Need.Food => job.def == JobDefOf.Ingest,
                Operations.Need.Rest => job.def == JobDefOf.LayDown,
                Operations.Need.Joy => job.def.joyKind != null,
                _ => false,
            };
        }

        private static Receipts.EffectEvidence Evidence(Pawn pawn, Job job, bool issued) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobId = job.loadID, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = pawn.GetUniqueLoadID() },
                CanTry = true, Issued = issued, Verified = true, Drafted = false,
            }
        };

        private static Common.Failure? Resolve(string pawnId, Operations.Need need, Common.ObservationContext context, out Pawn? pawn, out ThinkNode_JobGiver? giver)
        {
            pawn = null; giver = null;
            if (!ProtoBoundary.IsIdentifier(pawnId) || need == Operations.Need.Unspecified)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Need relief requires a pawn id and a need.");
            pawn = ProtoBoundary.LoadedMap(context).mapPawns.FreeColonistsSpawned.ById(pawnId);
            if (pawn == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact colonist is not spawned on this map.");
            if (pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState || !pawn.IsColonistPlayerControlled)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn unavailable, drafted or in an active mental break.");
            if (Running(pawn, need)) return null;
            if (HealthAIUtility.ShouldSeekMedicalRest(pawn))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Medical rest takes precedence.");
            if (!pawn.jobs.IsCurrentJobPlayerInterruptible() || pawn.carryTracker?.CarriedThing != null || pawn.IsBurning())
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current job, carried cargo or fire prevents safe interruption.");
            var level = NeedLevel(pawn, need);
            if (level == null || level >= 0.5f)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Need is absent or no longer deficient.");
            giver = Giver(need);
            if (giver == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Unknown need.");
            var assignment = pawn.timetable?.CurrentAssignment;
            if (need == Operations.Need.Joy
                    ? assignment != TimeAssignmentDefOf.Anything && assignment != TimeAssignmentDefOf.Joy
                    : giver.GetPriority(pawn) <= 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native need priority or player timetable prevents recovery now.");
            return null;
        }

        internal static Common.Failure? Validate(string pawnId, Operations.Need need, Common.ObservationContext context) => Resolve(pawnId, need, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(string pawnId, Operations.Need need, Common.ObservationContext context)
        {
            var failure = Resolve(pawnId, need, context, out var pawn, out var giver);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, need)) return Evidence(pawn!, pawn!.CurJob, false);
            giver!.ResolveReferences();
            var job = giver.TryIssueJobPackage(pawn!, default(JobIssueParams)).Job;
            if (job == null) throw new InvalidOperationException("No eligible native need job; inspect access, resources and recreation tolerance.");
            if (need == Operations.Need.Food && job.def != JobDefOf.Ingest)
                throw new InvalidOperationException("Food recovery requires an available ingestible; production remains a separate concern.");
            if (job.targetA.IsValid && (!pawn!.CanReach(job.targetA, PathEndMode.Touch, Danger.None) || job.targetA.Cell.IsForbidden(pawn)))
                throw new InvalidOperationException("Need target is not safely reachable under current restrictions.");
            if (!job.TryMakePreToilReservations(pawn!, errorOnFailed: false))
            { pawn!.ClearReservationsForJob(job); throw new InvalidOperationException("Native job reservations refused recovery."); }
            pawn!.jobs.StartJob(job, JobCondition.InterruptForced, giver, preToilReservationsCanFail: true);
            if (pawn.CurJob != job) throw new InvalidOperationException("The pawn did not start the need job.");
            return Evidence(pawn, job, true);
        }
    }
}
