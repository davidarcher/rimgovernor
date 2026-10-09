#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // GiveJobIntent job InteractThing: order one colonist to interact
    // with a void structure, the Gleaming monolith or the void node, the job
    // CompInteractable.OrderForceTarget gives (JobDefOf.InteractThing). The
    // whitelist is the Anomaly endgame's three comps (CompGleamingMonolith is a
    // CompVoidStructure); the game's own CanInteract(pawn) decides, live. The
    // target and the pawn share a map, which is the pocket map for the node:
    // the pawn the Gleaming monolith skips there stands drafted, and the order
    // is the one the game's float menu gives such a pawn. Applied means
    // ordered: the node's choice dialog opens when the interaction completes
    // and is answered as a dialog.
    internal static class NativeInteractOperations
    {
        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Thing? target, out bool running)
        {
            pawn = null; target = null; running = false;
            if (!NativePawnControlState.IsReady) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            foreach (var map in Find.Maps)
                if (RefIndex.Thing(map, intent.TargetId) is Thing found) { target = found; break; }
            if (target == null || !target.Spawned || target.Destroyed || target.Map == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact interactable thing is not spawned on a loaded map.");
            var comp = target.TryGetComp<CompInteractable>();
            if (comp is not CompVoidStructure && comp is not CompVoidNode)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Only a void structure, the Gleaming monolith or the void node can be interacted with.");
            var map2 = target.Map;
            pawn = map2.mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (pawn == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on the target's map.");
            var identity = new NativeControlIdentity(Current.Game, map2, context.Identity.ColonyId, context.Identity.LoadToken);
            var control = NativePawnControlState.Observe(identity, pawn, out var snapshot);
            if (control != NativePawnControlResult.Ready || snapshot == null) return NativeDraftProtocol.Failure(control, context);
            if (!snapshot.Eligible || snapshot.Drafted && comp is not CompVoidNode)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An interaction requires an eligible pawn, undrafted unless it is the void node's.");
            var current = pawn.CurJob;
            running = current != null && current.def == JobDefOf.InteractThing && current.targetA.Thing == target;
            if (running) return null;
            var verdict = comp!.CanInteract(pawn);
            if (!verdict.Accepted)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The pawn cannot interact now" + (verdict.Reason.NullOrEmpty() ? "." : ": " + verdict.Reason));
            return null;
        }

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var target, out var running);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (running) return NativeGiveJob.Evidence(pawn!, target!, pawn!.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.InteractThing, target);
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(pawn.CurJob, job))
                throw new InvalidOperationException("The pawn did not take the interaction job.");
            return NativeGiveJob.Evidence(pawn, target!, job, true);
        }
    }
}
