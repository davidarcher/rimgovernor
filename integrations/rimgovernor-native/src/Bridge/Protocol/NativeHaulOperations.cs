using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // HaulIntent on Actions/Apply: order one undrafted colonist to haul
    // one loose item with the job a player's "Prioritize hauling" click would
    // give, the real Hauling WorkGiver picking the storage. Native checks the
    // pawn and the item against live state and refuses with a reason. The
    // intent is idempotent: a pawn already hauling the item (walking to it or
    // carrying it) is applied again without a new order. Applied means
    // ordered, not delivered; the next planner review reads where the item is.
    internal static class NativeHaulOperations
    {
        internal const string Kind = "Haul";

        private static bool Hauling(Pawn pawn, Thing thing)
        {
            if (pawn.carryTracker?.CarriedThing == thing) return true;
            var job = pawn.CurJob;
            return job != null && (job.def == JobDefOf.HaulToCell || job.def == JobDefOf.HaulToContainer) && job.targetA.Thing == thing;
        }

        private static WorkGiverJobResult? Job(Pawn pawn, Thing thing, out string? reason) =>
            WorkGiverDispatch.TryJob(pawn, thing, def => def.workType == WorkTypeDefOf.Hauling, out reason);

        // Resolve finds the pawn and item and checks them, one rule at a time
        // (action-contracts.md "Apply-time preconditions").
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.HaulIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out Thing? thing)
        {
            pawn = null; thing = null;
            if (intent == null || !GameThread.IsIdentifier(intent.PawnId) || !GameThread.IsIdentifier(intent.ThingId) || (intent.PawnId ?? "") == (intent.ThingId ?? ""))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Haul requires an exact pawn id and item id.");
            if (!NativePawnControlState.IsReady)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            var map = GameThread.LoadedMap(context);
            var identity = new NativeControlIdentity(Current.Game, map, (context.Identity?.ColonyId ?? ""), (context.Identity?.LoadToken ?? ""));
            var foundPawn = map.mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (foundPawn == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, ApplyPreconditions.Detail(Kind, "the exact pawn is not spawned on this map"));
            var control = NativePawnControlState.Observe(identity, foundPawn, out var observed);
            if (control != NativePawnControlResult.Ready || observed == null) return NativeDraftProtocol.Failure(control, context);
            var carried = foundPawn.carryTracker?.CarriedThing;
            var foundThing = carried != null && RefIndex.Is(carried, intent.ThingId)
                ? carried
                : RefIndex.Thing(map, intent.ThingId);
            var facts = observed.Facts;
            var rules = new ApplyPreconditions(Kind)
                .Require(() => !facts.Dead && !facts.Downed, "the pawn is dead or downed")
                .Require(() => !facts.Mental, "the pawn is in a mental state")
                .Require(() => facts.PlayerControlled && facts.Drafter != null, "the pawn is not a player-controlled colonist")
                .Require(() => !observed.Drafted, "the pawn is drafted")
                .Present(() => foundThing != null && !foundThing.Destroyed, "the exact item no longer exists on this map")
                .Require(() => foundThing != null && (foundThing.def.EverHaulable && foundThing.def.category == ThingCategory.Item), "the target is not a haulable item");
            if (!rules.Holds || foundThing == null) return rules.Failure();
            pawn = foundPawn; thing = foundThing;
            if (Hauling(pawn, thing)) return null;
            if (!thing.Spawned || !GameThread.IsLoaded(thing.Map))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, ApplyPreconditions.Detail(Kind, "the item is not spawned on this map"));
            if (thing.Position.Fogged(thing.Map))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, ApplyPreconditions.Detail(Kind, "the item's cell is fogged"));
            if (Job(pawn, thing, out var reason) == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, ApplyPreconditions.Detail(Kind, "no hauling job is available for this pawn and item" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason)));
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence(Pawn pawn, Thing thing, Job? job, bool issued) => new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence
        {
            Effect = new RimGovernor.Brain.GameFacts.Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobDef = job?.def?.defName ?? "HaulToCell",
                TargetA = new RimGovernor.Brain.GameFacts.Receipts.JobTarget { Target = new RimGovernor.Brain.GameFacts.Receipts.JobTargetTarget.ThingIdCase(thing.GetUniqueLoadID()) },
                Issued = issued,
            }
        };

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.HaulIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) =>
            Resolve(intent, context, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.HaulIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var thing);
            if (failure != null || pawn == null || thing == null) throw new System.InvalidOperationException(failure?.Detail);
            if (Hauling(pawn, thing)) return Evidence(pawn, thing, pawn.CurJob, false);
            var result = Job(pawn, thing, out _) ?? throw new System.InvalidOperationException("No hauling job is available.");
            if (!pawn.jobs.TryTakeOrderedJobPrioritizedWork(result.Job, result.Scanner, thing.Position) || !ReferenceEquals(pawn.CurJob, result.Job))
                throw new System.InvalidOperationException("The pawn did not take the hauling job.");
            return Evidence(pawn, thing, result.Job, true);
        }
    }

    internal sealed class HaulActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeHaulOperations.Validate(action.Intent?.Haul, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeHaulOperations.Apply(action.Intent?.Haul, context);
    }
}
