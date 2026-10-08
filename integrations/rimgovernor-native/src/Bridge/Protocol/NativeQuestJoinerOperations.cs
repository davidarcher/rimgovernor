#nullable enable
using System;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // FloatMenuOptionProvider_OfferHelp labels this FreePrisoner for captives.
    // The vanilla driver walks to the pawn and joins them because rescued.
    internal static class NativeQuestJoinerOperations
    {
        private static Common.Failure? Resolve(JobOrder order, Common.ObservationContext context, out Pawn? actor, out Pawn? target, out bool running)
        {
            target = null; running = false;
            var failure = NativeGiveJob.Pawn(order, context, out actor, out var snapshot);
            if (failure != null) return failure;
            if (!snapshot!.Eligible || snapshot.Drafted || !actor!.IsColonistPlayerControlled || !actor.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Helping requires an eligible undrafted mobile colonist.");
            target = RefIndex.Thing(ProtoBoundary.LoadedMap(context), order.TargetId) as Pawn;
            if (target == null || !target.Spawned || target.Dead || target == actor || !target.mindState.WillJoinColonyIfRescued)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "The exact willing rescue joiner is unavailable.");
            running = actor.CurJob?.def == JobDefOf.OfferHelp && actor.CurJob.targetA.Thing == target;
            if (!running && !actor.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist cannot reach the rescue joiner.");
            return null;
        }
        internal static Common.Failure? Validate(JobOrder order, Common.ObservationContext context) => Resolve(order, context, out _, out _, out _);
        internal static Receipts.EffectEvidence Apply(JobOrder order, Common.ObservationContext context)
        {
            var failure = Resolve(order, context, out var actor, out var target, out var running);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (running) return NativeGiveJob.Evidence(actor!, target!, actor!.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.OfferHelp, target);
            if (!actor!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(actor.CurJob, job))
                throw new InvalidOperationException("The colonist did not take the native help job.");
            return NativeGiveJob.Evidence(actor, target!, job, true);
        }
    }
}
