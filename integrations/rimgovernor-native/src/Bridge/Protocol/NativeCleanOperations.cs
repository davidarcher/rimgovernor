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
    // GiveJobIntent Clean, the bounded cleaning response of
    // MaintainCleanFacilities: exact spawned filth inside the home area, an
    // undrafted worker who can reach it and needs no tending, plus
    // WorkGiver_CleanFilth's own HasJobOnThing (reservable, thickened at
    // least 600 ticks ago), then one JobDefOf.Clean job whose target queue
    // holds the exact filth and nothing else. The work giver's JobOnThing
    // would also sweep up to fifteen neighbouring filth into the same job;
    // the controller's order is bounded to the one target it chose, so the
    // job is built here. Like the float menu, the Work-tab priority is not
    // consulted: only WorkTags/capacity incapability refuses. Checked live at
    // apply; a pawn already cleaning the filth applies again.
    internal static class NativeCleanOperations
    {
        internal static WorkGiver_CleanFilth? Giver() => DefDatabase<WorkGiverDef>.AllDefsListForReading
            .Where(d => d.giverClass != null && typeof(WorkGiver_CleanFilth).IsAssignableFrom(d.giverClass))
            .Select(d => d.Worker as WorkGiver_CleanFilth).FirstOrDefault(w => w != null);

        private static bool Running(Pawn pawn, Thing filth) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Clean
            && (pawn.CurJob.targetA.Thing == filth || pawn.CurJob.GetTargetQueue(TargetIndex.A).Any(t => t.Thing == filth));

        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Filth? filth)
        {
            filth = null;
            var failure = NativeGiveJob.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (snapshot!.Drafted || !snapshot.Eligible)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Clean requires an eligible undrafted pawn.");
            var map = ProtoBoundary.LoadedMap(context);
            filth = RefIndex.Thing(map, intent.TargetId) as Filth;
            if (filth == null || filth.Destroyed || !filth.Spawned || filth.Map != map)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact spawned filth is unavailable.");
            if (Running(pawn!, filth)) return null;
            if (!map.areaManager.Home[filth.Position])
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Clean targets only filth inside the current home area.");
            if (pawn!.health.HasHediffsNeedingTend())
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn needs tending and cannot be ordered to clean.");
            var giver = Giver();
            var giverDef = giver?.def;
            if (giver == null || giverDef == null)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "WorkGiver_CleanFilth is unavailable in this game.");
            var missing = giver.MissingRequiredCapacity(pawn);
            if (missing != null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn is missing a capacity cleaning needs (" + missing.defName + ").");
            if (pawn.WorkTagIsDisabled(giverDef.workTags) || (giverDef.workType != null && pawn.WorkTypeIsDisabled(giverDef.workType)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn is incapable of cleaning work.");
            if (!pawn.CanReach(filth, PathEndMode.Touch, Danger.None))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot safely reach the filth.");
            if (!giver.HasJobOnThing(pawn, filth, true))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The native clean work giver refuses this filth for this pawn right now (reserved, or thickened within the last 600 ticks).");
            return null;
        }

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var filth);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, filth!)) return NativeGiveJob.Evidence(pawn!, filth!, pawn!.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.Clean);
            job.AddQueuedTarget(TargetIndex.A, filth);
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID)
                throw new InvalidOperationException("The pawn did not take the clean job.");
            return NativeGiveJob.Evidence(pawn, filth!, job, true);
        }
    }
}
