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
    // RecoverIntent on Actions/Apply (#940): order one undrafted colonist to
    // repair, fix a breakdown or refuel one colony building with the job the
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

        // Mirrors RecoveryTools.Order's "observed service no longer needs this
        // method" refusal: repair only while under max HP, breakdown only while
        // broken, refuel only while under a quarter of the target fuel level,
        // the controller's own refuel threshold (policy.RecoveryWork orders a
        // refuel below 25%, the defensive layout's rearm on an empty barrel).
        // One refuel job carries at most a pawn's load (75 steel fills a mini
        // turret barrel to 56 of 60, #205), so a full-barrel criterion would
        // read a successful order as interrupted and never re-issue it.
        internal const float RefuelSatisfiedFraction = 0.25f;

        internal static bool Satisfied(Building building, Operations.ServiceMethod method)
        {
            switch (method)
            {
                case Operations.ServiceMethod.Repair:
                    return !building.def.useHitPoints || building.HitPoints >= building.MaxHitPoints;
                case Operations.ServiceMethod.Breakdown:
                    return building.TryGetComp<CompBreakdownable>()?.BrokenDown != true;
                case Operations.ServiceMethod.Refuel:
                    var fuel = building.TryGetComp<CompRefuelable>();
                    return fuel == null || fuel.Fuel >= fuel.TargetFuelLevel || fuel.Fuel >= RefuelSatisfiedFraction * fuel.TargetFuelLevel;
                default:
                    return true;
            }
        }

        // The giver is matched by class assignability: Core rearms a turret
        // barrel through WorkGiver_Refuel_Turret, a WorkGiver_Refuel subclass
        // on the RearmTurrets def, while the base class skips turrets (#205).
        private static Type? WorkGiverType(Operations.ServiceMethod method) => method switch
        {
            Operations.ServiceMethod.Repair => typeof(WorkGiver_Repair),
            Operations.ServiceMethod.Breakdown => typeof(WorkGiver_FixBrokenDownBuilding),
            Operations.ServiceMethod.Refuel => typeof(WorkGiver_Refuel),
            _ => null,
        };

        private static Common.Failure? Resolve(Operations.RecoverIntent? intent, Common.ObservationContext context, out Pawn? pawn, out Building? building, out WorkGiverJobResult? job)
        {
            pawn = null; building = null; job = null;
            var giverType = intent == null ? null : WorkGiverType(intent.Method);
            if (intent == null || !ProtoBoundary.IsIdentifier(intent.PawnId) || !ProtoBoundary.IsIdentifier(intent.ThingId) || giverType == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Service order requires an exact pawn, exact building target and a repair/breakdown/refuel method.");
            if (!NativePawnControlState.IsReady) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            var map = ProtoBoundary.LoadedMap(context);
            var identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = map.mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (pawn == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map.");
            var control = NativePawnControlState.Observe(identity, pawn, out var snapshot);
            if (control != NativePawnControlResult.Ready || snapshot == null) return NativeDraftProtocol.Failure(control, context);
            if (snapshot.Drafted || !snapshot.Eligible) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Service order requires an eligible undrafted pawn.");
            building = map.listerBuildings.allBuildingsColonist.ById(intent.ThingId);
            if (building == null || !Eligible(building)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact serviceable building is unavailable.");
            if (Servicing(pawn, building, giverType)) return null;
            if (Satisfied(building, intent.Method)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Observed service no longer needs this method.");
            job = WorkGiverDispatch.TryJob(pawn, building, def => giverType.IsAssignableFrom(def.giverClass), out _);
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

        internal static Common.Failure? Validate(Operations.RecoverIntent? intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.RecoverIntent? intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var building, out var job);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (job == null) return Evidence(pawn!, building!, pawn!.CurJob, false);
            if (!pawn!.jobs.TryTakeOrderedJobPrioritizedWork(job.Job, job.Scanner, building!.Position) || !ReferenceEquals(pawn.CurJob, job.Job))
                throw new InvalidOperationException("The pawn did not take the service job.");
            return Evidence(pawn, building!, job.Job, true);
        }
    }

    internal sealed class RecoverActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeRecoveryOperations.Validate(action.Recover, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeRecoveryOperations.Apply(action.Recover, context);
    }
}
