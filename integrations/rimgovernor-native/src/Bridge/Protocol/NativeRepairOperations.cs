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
    // PawnOrderIntent kind REPAIR (#939), the bounded repair response of
    // MaintainEssentialRepairs: an exact damaged player building, an
    // undrafted eligible worker who can reach it and needs no tending,
    // WorkGiver_Repair's own HasJobOnThing (home area, reservable, not
    // burning, no deconstruct/uninstall designation), then the work giver's
    // JobDefOf.Repair job issued as a player-forced order. Checked live at
    // apply; a pawn already repairing the building applies again.
    internal static class NativeRepairOperations
    {
        private static bool Running(Pawn pawn, Thing target) => pawn.CurJob != null && pawn.CurJob.def == JobDefOf.Repair && pawn.CurJob.targetA.Thing == target;

        private static Common.Failure? Resolve(Operations.PawnOrderIntent intent, Common.ObservationContext context, out Pawn? pawn, out Building? building, out WorkGiverJobResult? job)
        {
            pawn = null; building = null; job = null;
            var failure = NativePawnOrderIntent.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            if (snapshot!.Drafted || !snapshot.Eligible)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Repair requires an eligible undrafted pawn.");
            var map = ProtoBoundary.LoadedMap(context);
            building = map.listerBuildings.allBuildingsColonist.SingleOrDefault(b => b.GetUniqueLoadID() == intent.TargetId);
            if (building == null || building.Destroyed || !building.Spawned || building.Map != map)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact spawned player building is unavailable.");
            if (Running(pawn!, building)) return null;
            if (!building.def.useHitPoints || building.HitPoints >= building.MaxHitPoints)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Building is not damaged.");
            if (pawn!.health.HasHediffsNeedingTend())
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn needs tending and cannot be ordered to repair.");
            if (!pawn.CanReach(building, PathEndMode.Touch, Danger.None))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn cannot safely reach the building.");
            // WorkGiver_Repair's HasJobOnThing carries the remaining gates (home
            // area, reservation, burning, pending deconstruction/uninstall) and
            // WorkGiverDispatch adds the float-menu capability refusals.
            job = WorkGiverDispatch.TryJob(pawn, building, def => def.giverClass == typeof(WorkGiver_Repair), out var reason);
            if (job == null || job.Job.def != JobDefOf.Repair)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "WorkGiver_Repair refuses this building for this pawn right now" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason));
            return null;
        }

        internal static Common.Failure? Validate(Operations.PawnOrderIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.PawnOrderIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var building, out var result);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (Running(pawn!, building!)) return NativePawnOrderIntent.Evidence(pawn!, building!, pawn!.CurJob, false);
            var job = result!.Job;
            if (!pawn!.jobs.TryTakeOrderedJobPrioritizedWork(job, result.Scanner, building!.Position) || pawn.CurJob == null || pawn.CurJob.loadID != job.loadID)
                throw new InvalidOperationException("The pawn did not take the repair job.");
            return NativePawnOrderIntent.Evidence(pawn, building, job, true);
        }
    }
}
