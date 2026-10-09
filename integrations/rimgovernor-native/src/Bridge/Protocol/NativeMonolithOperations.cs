#nullable enable
using System;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // GiveJobIntent jobs InvestigateMonolith and ActivateMonolith: order
    // one colonist to the void monolith with the job the game's own float menu
    // gives (Building_VoidMonolith.GetFloatMenuOptions). Investigate is the
    // Inactive level's order, Activate every later level's; the game's own
    // CanActivate and ValidateTarget decide, live. Applied means ordered: the
    // investigate dialog and the awakening confirmation open when the job runs
    // and are answered as dialogs.
    internal static class NativeMonolithOperations
    {
        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Building_VoidMonolith? monolith, out bool running)
        {
            monolith = null; running = false;
            var failure = NativeGiveJob.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (snapshot!.Drafted || !snapshot.Eligible) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A monolith order requires an eligible undrafted pawn.");
            var map = ProtoBoundary.LoadedMap(context);
            monolith = RefIndex.Thing(map, intent.TargetId) as Building_VoidMonolith;
            if (monolith == null || !monolith.Spawned || monolith.Destroyed || !ReferenceEquals(monolith, Find.Anomaly.monolith))
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact void monolith is not spawned on this map.");
            var investigate = intent.Kind == JobOrderKind.InvestigateMonolith;
            var current = pawn!.CurJob;
            running = current != null && current.def == (investigate ? JobDefOf.InvestigateMonolith : JobDefOf.ActivateMonolith) && current.targetA.Thing == monolith;
            if (running) return null;
            if (investigate != (Find.Anomaly.Level == 0))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, investigate ? "The monolith is no longer inactive." : "An inactive monolith is investigated, not activated.");
            if (!monolith.CanActivate(out var reason, out _))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The monolith cannot be activated now" + (reason.NullOrEmpty() ? "." : ": " + reason));
            if (!monolith.ValidateTarget(pawn, showMessages: false))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The pawn cannot take the monolith job now.");
            return null;
        }

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var monolith, out var running);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (running) return NativeGiveJob.Evidence(pawn!, monolith!, pawn!.CurJob, false);
            var job = JobMaker.MakeJob(intent.Kind == JobOrderKind.InvestigateMonolith ? JobDefOf.InvestigateMonolith : JobDefOf.ActivateMonolith, monolith);
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(pawn.CurJob, job))
                throw new InvalidOperationException("The pawn did not take the monolith job.");
            return NativeGiveJob.Evidence(pawn, monolith!, job, true);
        }
    }
}
