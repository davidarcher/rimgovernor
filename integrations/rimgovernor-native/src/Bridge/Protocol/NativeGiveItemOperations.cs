#nullable enable
using System;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Expected remaining fences the whole vanilla request, never a delivery cap.
    internal static class NativeGiveItemOperations
    {
        internal static bool ValidShape(Operations.GiveItemIntent? c) => c != null && c.Hauler != null && c.Recipient != null
            && c.HasDefinition && c.HasExpectedRemaining && c.ExpectedRemaining > 0 && c.ExpectedRemaining <= int.MaxValue
            && ProtoBoundary.IsIdentifier(c.Hauler.Id) && ProtoBoundary.IsIdentifier(c.Recipient.Id)
            && c.Hauler.Id != c.Recipient.Id && Allowed(c.Definition);
        internal static Common.Failure? Validate(Operations.GiveItemIntent c, Common.ObservationContext context) => !ValidShape(c)
            ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An exact whole item request is required.")
            : Validate(c.Hauler.Id, c.Recipient.Id, c.Definition, (int)c.ExpectedRemaining, context);
        internal static Receipts.EffectEvidence Apply(Operations.GiveItemIntent c, Common.ObservationContext context)
        {
            var failure = Validate(c, context);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            return Apply(c.Hauler.Id, c.Recipient.Id, c.Definition, (int)c.ExpectedRemaining, context);
        }
        private static Receipts.EffectEvidence Evidence(Pawn hauler, Pawn recipient, Thing source, Job job, int remaining, bool issued)
            => new Receipts.EffectEvidence { GiveItem = new Receipts.GiveItemEffect {
                HaulerId = hauler.GetUniqueLoadID(), RecipientId = recipient.GetUniqueLoadID(), Definition = source.def.defName,
                ExpectedRemaining = remaining, SourceItemId = source.GetUniqueLoadID(), Job = NativeGiveJob.Evidence(hauler, source, job, issued).Job } };
        private static bool Allowed(string def) => def == "Silver" || def == "MedicineHerbal" || def == "MedicineIndustrial" || def == "Penoxycyline" || def == "Beer";
        private static Common.Failure? Resolve(string haulerId, string recipientId, string defName, int expectedRemaining, Common.ObservationContext context, out Pawn? hauler, out Pawn? recipient, out Thing? source, out Lord? lord, out bool running)
        {
            hauler = null; recipient = null; source = null; lord = null; running = false;
            if (!ProtoBoundary.IsIdentifier(haulerId) || !ProtoBoundary.IsIdentifier(recipientId) || haulerId == recipientId || !Allowed(defName) || expectedRemaining <= 0)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Giving requires distinct exact pawns, an allowed item and a positive expected remaining request.");
            var failure = NativeGiveJob.Pawn(new JobOrder { PawnId = haulerId, TargetId = recipientId }, context, out hauler, out var snapshot);
            if (failure != null) return failure;
            if (!snapshot!.Eligible || snapshot.Drafted || !hauler!.IsColonistPlayerControlled || !hauler.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Giving requires an eligible undrafted hauler.");
            var map = ProtoBoundary.LoadedMap(context);
            recipient = RefIndex.Thing(map, recipientId) as Pawn;
            if (recipient == null || recipient.Dead || !recipient.Spawned || recipient.IsForbidden(hauler))
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The exact recipient is unavailable.");
            lord = recipient.GetLord();
            ThingDef? requested = null;
            Pawn? target = null;
            if (lord?.CurLordToil is LordToil_WaitForItems wait) { target = wait.target; requested = wait.requestedThingDef; }
            else if (lord?.CurLordToil is LordToil_TravelAndWaitForItems travel) { target = travel.target; requested = travel.requestedThingDef; }
            if (target != recipient || requested?.defName != defName || GiveItemsToPawnUtility.ItemCountLeftToCollect(recipient) != expectedRemaining)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The native item request or expected remaining count changed.");
            var current = hauler.CurJob;
            running = current?.def == JobDefOf.GiveToPawn && current.targetB.Pawn == recipient && current.targetA.Thing?.def == requested && current.lord == lord;
            if (running) { source = current!.targetA.Thing; return null; }
            if (!hauler.CanReach(recipient, PathEndMode.Touch, Danger.Deadly))
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The hauler cannot reach the recipient.");
            source = GiveItemsToPawnUtility.FindItemToGive(hauler, requested!);
            if (source == null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No native reachable item is available to give.");
            return null;
        }

        internal static Common.Failure? Validate(string haulerId, string recipientId, string defName, int expectedRemaining, Common.ObservationContext context) => Resolve(haulerId, recipientId, defName, expectedRemaining, context, out _, out _, out _, out _, out _);
        internal static Receipts.EffectEvidence Apply(string haulerId, string recipientId, string defName, int expectedRemaining, Common.ObservationContext context)
        {
            var failure = Resolve(haulerId, recipientId, defName, expectedRemaining, context, out var hauler, out var recipient, out var source, out var lord, out var running);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (running) return Evidence(hauler!, recipient!, source!, hauler!.CurJob, expectedRemaining, false);
            var job = JobMaker.MakeJob(JobDefOf.GiveToPawn, source, recipient);
            job.haulMode = HaulMode.ToContainer;
            job.lord = lord;
            if (!hauler!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(hauler.CurJob, job))
                throw new InvalidOperationException("The hauler did not take the native giving job.");
            return Evidence(hauler, recipient!, source!, job, expectedRemaining, true);
        }
    }
    internal sealed class GiveItemActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeGiveItemOperations.Validate(action.GiveItem, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeGiveItemOperations.Apply(action.GiveItem, context);
    }
}
