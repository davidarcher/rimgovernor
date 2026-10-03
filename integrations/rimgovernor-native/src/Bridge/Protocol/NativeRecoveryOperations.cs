#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // GiveJobIntent jobs FixBrokenDownBuilding and Refuel (#940, #1351):
    // order one undrafted colonist to fix a breakdown on or refuel one colony
    // building with the job the
    // game's own WorkGiver builds (the one a player's prioritize click
    // gives). Native checks the pawn, the building and whether it still
    // needs the method live; a pawn already servicing the building applies
    // again. Applied means ordered; the next recovery census reads the
    // building.
    internal static class NativeRecoveryOperations
    {
        internal static bool Eligible(Building? building) => building != null && !building.Destroyed && building.Spawned
            && ProtoBoundary.IsLoaded(building.Map) && !building.Position.Fogged(building.Map)
            && !building.IsForbidden(Faction.OfPlayer) && !building.IsBurning();

        // Refuses when the "observed service no longer needs this
        // method" refusal: repair only while under max HP, breakdown only while
        // broken, refuel only while under a quarter of the target fuel level,
        // the controller's own refuel threshold (policy.RecoveryWork orders a
        // refuel below 25%, the defensive layout's rearm on an empty barrel).
        // One refuel job carries at most a pawn's load (75 steel fills a mini
        // turret barrel to 56 of 60, #205), so a full-barrel criterion would
        // read a successful order as interrupted and never re-issue it.
        internal const float RefuelSatisfiedFraction = 0.25f;

        internal static bool Satisfied(Building building, JobOrderKind method)
        {
            switch (method)
            {
                case JobOrderKind.FixBreakdown:
                    return building.TryGetComp<CompBreakdownable>()?.BrokenDown != true;
                case JobOrderKind.Refuel:
                    var fuel = building.TryGetComp<CompRefuelable>();
                    return fuel == null || fuel.Fuel >= fuel.TargetFuelLevel || fuel.Fuel >= RefuelSatisfiedFraction * fuel.TargetFuelLevel;
                default:
                    return true;
            }
        }

        // The giver is matched by class assignability: Core rearms a turret
        // barrel through WorkGiver_Refuel_Turret, a WorkGiver_Refuel subclass
        // on the RearmTurrets def, while the base class skips turrets (#205).
        private static Type? WorkGiverType(JobOrderKind method) => method switch
        {
            JobOrderKind.FixBreakdown => typeof(WorkGiver_FixBrokenDownBuilding),
            JobOrderKind.Refuel => typeof(WorkGiver_Refuel),
            _ => null,
        };

        private static Common.Failure? Resolve(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out Building? building, out WorkGiverJobResult? job)
        {
            building = null; job = null;
            var giverType = WorkGiverType(intent.Kind)!;
            var failure = NativeGiveJob.Pawn(intent, context, out pawn, out var snapshot);
            if (failure != null) return failure;
            var map = ProtoBoundary.LoadedMap(context);
            if (snapshot!.Drafted || !snapshot.Eligible) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Service order requires an eligible undrafted pawn.");
            building = map.listerBuildings.allBuildingsColonist.ById(intent.TargetId);
            if (building == null || !Eligible(building)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact serviceable building is unavailable.");
            if (Servicing(pawn!, building, giverType)) return null;
            if (Satisfied(building, intent.Kind)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Observed service no longer needs this method.");
            job = WorkGiverDispatch.TryJob(pawn!, building, def => giverType.IsAssignableFrom(def.giverClass), out _);
            if (job == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No native service job is available for this pawn and target.");
            return null;
        }

        private static bool Servicing(Pawn pawn, Building building, Type giverType)
        {
            var current = pawn.CurJob;
            return current != null && current.targetA.Thing == building && current.workGiverDef != null && giverType.IsAssignableFrom(current.workGiverDef.giverClass);
        }

        private static Receipts.EffectEvidence Evidence(Pawn pawn, Building building, Job job, bool issued) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobId = job.loadID, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = building.GetUniqueLoadID() },
                CanTry = true, Issued = issued, Verified = true, Drafted = false,
            }
        };

        internal static Common.Failure? Validate(JobOrder intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(JobOrder intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var building, out var job);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (job == null) return Evidence(pawn!, building!, pawn!.CurJob, false);
            if (!pawn!.jobs.TryTakeOrderedJobPrioritizedWork(job.Job, job.Scanner, building!.Position) || !ReferenceEquals(pawn.CurJob, job.Job))
                throw new InvalidOperationException("The pawn did not take the service job.");
            return Evidence(pawn, building!, job.Job, true);
        }
    }
}
