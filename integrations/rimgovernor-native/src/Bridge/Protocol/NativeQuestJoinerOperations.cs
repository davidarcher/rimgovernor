using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // FloatMenuOptionProvider_OfferHelp labels this FreePrisoner for captives.
    // The vanilla driver walks to the pawn and joins them because rescued.
    internal static class NativeQuestJoinerOperations
    {
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(JobOrder order, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? actor, out Pawn? target, out bool running)
        {
            target = null; running = false;
            if (!NativeGiveJob.TryPawn(order, context, out actor, out var snapshot, out var failure)) return failure;
            if (!snapshot.Eligible || snapshot.Drafted || !actor.IsColonistPlayerControlled || !actor.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Helping requires an eligible undrafted mobile colonist.");
            target = RefIndex.Thing(GameThread.LoadedMap(context), order.TargetId) as Pawn;
            if (target == null || !target.Spawned || target.Dead || target == actor || !target.mindState.WillJoinColonyIfRescued)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "The exact willing rescue joiner is unavailable.");
            running = actor.CurJob?.def == JobDefOf.OfferHelp && actor.CurJob.targetA.Thing == target;
            if (!running && !actor.CanReach(target, PathEndMode.Touch, Danger.Deadly))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The colonist cannot reach the rescue joiner.");
            return null;
        }
        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(JobOrder order, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(order, context, out _, out _, out _);
        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(JobOrder order, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(order, context, out var actor, out var target, out var running);
            if (failure != null || actor == null || target == null) throw new InvalidOperationException(failure?.Detail);
            if (running) return NativeGiveJob.Evidence(actor, target, actor.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.OfferHelp, target);
            if (!actor.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(actor.CurJob, job))
                throw new InvalidOperationException("The colonist did not take the native help job.");
            return NativeGiveJob.Evidence(actor, target, job, true);
        }
    }
}
