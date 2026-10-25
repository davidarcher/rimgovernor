using System;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimGovernor.Native
{
    // Expected remaining fences the whole vanilla request, never a delivery cap.
    internal static class NativeGiveItemOperations
    {
        internal static bool ValidShape(RimGovernor.Brain.GameOrders.Operations.GiveItemIntent? c) => c != null && c.Hauler != null && c.Recipient != null
            && c.Definition is not null && c.ExpectedRemaining > 0 && c.ExpectedRemaining <= int.MaxValue
            && GameThread.IsIdentifier(c.Hauler.Id) && GameThread.IsIdentifier(c.Recipient.Id)
            && (c.Hauler.Id ?? "") != (c.Recipient.Id ?? "") && GameThread.IsIdentifier(c.Definition);
        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.GiveItemIntent? c, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => c is not { Hauler: { } hauler, Recipient: { } recipient } || !ValidShape(c)
            ? GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "An exact whole item request is required.")
            : Validate((hauler.Id ?? ""), (recipient.Id ?? ""), (c.Definition ?? ""), (int)c.ExpectedRemaining, context);
        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.GiveItemIntent? c, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Validate(c, context);
            if (failure != null) throw new ApplyRefusedException(failure);
            return Apply((((c ?? throw new System.InvalidOperationException("Required native value is unavailable: c.")).Hauler ?? throw new System.InvalidOperationException("Required native value is unavailable: (c ?? throw new System.InvalidOperationException(\"Required native value is unavailable: c.\")).Hauler.")).Id ?? ""), ((c.Recipient ?? throw new System.InvalidOperationException("Required native value is unavailable: c.Recipient.")).Id ?? ""), (c.Definition ?? ""), (int)c.ExpectedRemaining, context);
        }
        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence(Pawn recipient, int remaining)
            => new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.GiveItemEffect {
                RecipientId = recipient.GetUniqueLoadID(), ExpectedRemaining = remaining } };
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(string haulerId, string recipientId, string defName, int expectedRemaining, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? hauler, out Pawn? recipient, out Thing? source, out Lord? lord, out bool running)
        {
            hauler = null; recipient = null; source = null; lord = null; running = false;
            if (!GameThread.IsIdentifier(haulerId) || !GameThread.IsIdentifier(recipientId) || haulerId == recipientId || expectedRemaining <= 0)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Giving requires distinct exact pawns, an exact item and a positive expected remaining request.");
            if (!NativeGiveJob.TryPawn(new JobOrder { PawnId = haulerId, TargetId = recipientId }, context, out hauler, out var snapshot, out var failure)) return failure;
            if (!snapshot.Eligible || snapshot.Drafted || !hauler.IsColonistPlayerControlled || !hauler.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Giving requires an eligible undrafted hauler.");
            var map = GameThread.LoadedMap(context);
            recipient = RefIndex.Thing(map, recipientId) as Pawn;
            if (recipient == null || recipient.Dead || !recipient.Spawned || recipient.IsForbidden(hauler))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The exact recipient is unavailable.");
            lord = recipient.GetLord();
            ThingDef? requested = null;
            Pawn? target = null;
            if (lord?.CurLordToil is LordToil_WaitForItems wait) { target = wait.target; requested = wait.requestedThingDef; }
            else if (lord?.CurLordToil is LordToil_TravelAndWaitForItems travel) { target = travel.target; requested = travel.requestedThingDef; }
            if (target != recipient || requested?.defName != defName || GiveItemsToPawnUtility.ItemCountLeftToCollect(recipient) != expectedRemaining)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The native item request or expected remaining count changed.");
            var current = hauler.CurJob;
            running = current?.def == JobDefOf.GiveToPawn && current.targetB.Pawn == recipient && current.targetA.Thing?.def == requested && current.lord == lord;
            if (running) { source = (current ?? throw new System.InvalidOperationException("Required native value is unavailable: current.")).targetA.Thing; return null; }
            if (!hauler.CanReach(recipient, PathEndMode.Touch, Danger.Deadly))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The hauler cannot reach the recipient.");
            source = GiveItemsToPawnUtility.FindItemToGive(hauler, requested);
            if (source == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "No native reachable item is available to give.");
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(string haulerId, string recipientId, string defName, int expectedRemaining, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(haulerId, recipientId, defName, expectedRemaining, context, out _, out _, out _, out _, out _);
        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(string haulerId, string recipientId, string defName, int expectedRemaining, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(haulerId, recipientId, defName, expectedRemaining, context, out var hauler, out var recipient, out var source, out var lord, out var running);
            if (failure != null || hauler == null || recipient == null) throw new InvalidOperationException(failure?.Detail);
            if (running) return Evidence(recipient, expectedRemaining);
            var job = JobMaker.MakeJob(JobDefOf.GiveToPawn, source, recipient);
            job.haulMode = HaulMode.ToContainer;
            job.lord = lord;
            if (!hauler.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(hauler.CurJob, job))
                throw new InvalidOperationException("The hauler did not take the native giving job.");
            return Evidence(recipient, expectedRemaining);
        }
    }
    internal sealed class GiveItemActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeGiveItemOperations.Validate(action.Intent?.GiveItem, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeGiveItemOperations.Apply(action.Intent?.GiveItem, context);
    }
}
