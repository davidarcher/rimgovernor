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
    // PawnOrderIntent kind WEAR (#939), the gear family's wear order: the
    // pawn's availability, the apparel's eligibility and a material native
    // gain are checked live, then the exact JobDefOf.Wear job an apparel
    // float-menu order would produce is issued as ordered work so no
    // forced-outfit entry is created. Weapons go through EQUIP. A pawn
    // already wearing the apparel, or walking to wear it, applies again.
    internal static class NativeGearOperations
    {
        private static bool Worn(Pawn pawn, Apparel apparel) => pawn.apparel != null && pawn.apparel.WornApparel.Contains(apparel);
        private static bool Running(Pawn pawn, Apparel apparel) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Wear && pawn.CurJob.targetA.Thing == apparel;

        private static Common.Failure? Resolve(Operations.PawnOrderIntent intent, Common.ObservationContext context, out Pawn? pawn, out Apparel? apparel)
        {
            apparel = null;
            var failure = NativePawnOrderIntent.Pawn(intent, context, out pawn, out _);
            if (failure != null) return failure;
            if (!pawn!.IsFreeColonist) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact free colonist is not spawned on this map.");
            apparel = pawn.apparel?.WornApparel.SingleOrDefault(a => a.GetUniqueLoadID() == intent.TargetId);
            if (apparel != null) return null;
            apparel = ProtoBoundary.LoadedMap(context).listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().SingleOrDefault(a => a.GetUniqueLoadID() == intent.TargetId);
            if (apparel == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact loose apparel is unavailable.");
            if (Running(pawn, apparel)) return null;
            var blocked = GearUpkeepTools.Available(pawn);
            if (blocked != null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Wear requires an available pawn: " + blocked);
            var refusal = GearUpkeepTools.Eligible(pawn, apparel);
            if (refusal != null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot wear the apparel: " + refusal);
            if (GearUpkeepTools.Gain(pawn, apparel) < .05f)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No material native apparel improvement.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.PawnOrderIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.PawnOrderIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var apparel);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, apparel!)) return NativePawnOrderIntent.Evidence(pawn!, apparel!, pawn!.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
            if (Worn(pawn!, apparel!)) return NativePawnOrderIntent.Evidence(pawn!, apparel!, job, false);
            // Ordered work, not a forced outfit entry: the outfit policy keeps
            // deciding what the pawn may drop later.
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !Worn(pawn, apparel!) && (pawn.CurJob == null || pawn.CurJob.loadID != job.loadID))
                throw new InvalidOperationException("The pawn did not take the wear job.");
            return NativePawnOrderIntent.Evidence(pawn, apparel!, job, true);
        }
    }
}
