using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // GiveJobIntent Wear, the gear family's wear order: the
    // pawn's availability, the apparel's eligibility are checked live, then the exact JobDefOf.Wear job an apparel
    // float-menu order would produce is issued as ordered work so no
    // forced-outfit entry is created. Weapons go through EQUIP. A pawn
    // already wearing the apparel, or walking to wear it, applies again.
    internal static class NativeGearOperations
    {
        private static bool Worn(Pawn pawn, Apparel apparel) => pawn.apparel != null && pawn.apparel.WornApparel.Contains(apparel);
        private static bool Running(Pawn pawn, Apparel apparel) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Wear && pawn.CurJob.targetA.Thing == apparel;

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out Apparel? apparel)
        {
            apparel = null;
            if (!NativeGiveJob.TryPawn(intent, context, out pawn, out _, out var failure)) return failure;
            if (!pawn.IsFreeColonist) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact free colonist is not spawned on this map.");
            apparel = pawn.apparel?.WornApparel.ById(intent.TargetId);
            if (apparel != null) return null;
            apparel = GameThread.LoadedMap(context).listerThings.ThingsInGroup(ThingRequestGroup.Apparel).OfType<Apparel>().ById(intent.TargetId);
            if (apparel == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact loose apparel is unavailable.");
            if (Running(pawn, apparel)) return null;
            var blocked = GearUpkeepTools.Available(pawn);
            if (blocked != null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Wear requires an available pawn: " + blocked);
            var refusal = GearUpkeepTools.Eligible(pawn, apparel);
            if (refusal != null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Pawn cannot wear the apparel: " + refusal);
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var apparel);
            if (failure != null || pawn == null || apparel == null) throw new InvalidOperationException(failure?.Detail);
            if (Running(pawn, apparel)) return NativeGiveJob.Evidence(pawn, apparel, pawn.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
            if (Worn(pawn, apparel)) return NativeGiveJob.Evidence(pawn, apparel, job, false);
            // Ordered work, not a forced outfit entry: the outfit policy keeps
            // deciding what the pawn may drop later.
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !Worn(pawn, apparel) && (pawn.CurJob == null || pawn.CurJob.loadID != job.loadID))
                throw new InvalidOperationException("The pawn did not take the wear job.");
            return NativeGiveJob.Evidence(pawn, apparel, job, true);
        }
    }
}
