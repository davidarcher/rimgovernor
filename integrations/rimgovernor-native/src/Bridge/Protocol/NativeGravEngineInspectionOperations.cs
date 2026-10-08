#nullable enable
using System;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The exact job issued by Building_GravEngine.GetFloatMenuOptions. Its
    // driver reserves and reaches the engine, then calls Inspect after work.
    internal static class NativeGravEngineInspectionOperations
    {
        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Building_GravEngine? engine, out bool running)
        {
            engine = null; running = false;
            var failure = NativeGiveJob.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (!snapshot!.Eligible || snapshot.Drafted || !pawn!.IsColonistPlayerControlled || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Inspection requires an eligible undrafted mobile colonist.");
            var map = ProtoBoundary.LoadedMap(context);
            engine = RefIndex.Thing(map, intent.TargetId) as Building_GravEngine;
            if (engine == null || !engine.Spawned || engine.Destroyed || engine.Position.Fogged(map))
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact grav engine is not visible on this map.");
            if (Find.ResearchManager.gravEngineInspected)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The grav engine has already been inspected.");
            var current = pawn.CurJob;
            running = current != null && current.def == JobDefOf.InspectGravEngine && current.targetA.Thing == engine;
            if (running) return null;
            if (!pawn.CanReserveAndReach(engine, PathEndMode.Touch, Danger.None))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The colonist cannot reserve and reach the grav engine.");
            return null;
        }

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var engine, out var running);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (running) return NativeGiveJob.Evidence(pawn!, engine!, pawn!.CurJob, false);
            var job = JobMaker.MakeJob(JobDefOf.InspectGravEngine, engine);
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(pawn.CurJob, job))
                throw new InvalidOperationException("The colonist did not take the grav engine inspection job.");
            return NativeGiveJob.Evidence(pawn, engine!, job, true);
        }
    }
}
