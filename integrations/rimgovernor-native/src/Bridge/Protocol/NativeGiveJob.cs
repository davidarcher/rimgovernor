#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The single-target jobs a GiveJobIntent names, each validated and built
    // by its own operations class against live state.
    internal enum JobOrderKind { Repair, Clean, OpenCasket, Tend, Equip, Wear, Rescue, Capture, Arrest, Subdue, FixBreakdown, Refuel, Waste }

    // One pawn, one target (and Arrest's bed) for a JobOrderKind.
    internal sealed class JobOrder
    {
        internal JobOrderKind Kind;
        internal string PawnId = "", TargetId = "";
        internal string? BedId;
        // HaulWaste only: the caller declares the item unwanted waste.
        internal bool Unwanted;
        internal bool HasBedId => BedId != null;
    }

    // The GiveJobIntent arm of Actions/Apply (#1352): one pawn, one vanilla
    // job, its targets in job target order. Go chose the pawn and the
    // targets; native checks the game rules live and builds the game's job.
    internal static class NativeGiveJob
    {
        private static readonly Dictionary<string, JobOrderKind> Kinds = new Dictionary<string, JobOrderKind>
        {
            ["Repair"] = JobOrderKind.Repair, ["Clean"] = JobOrderKind.Clean, ["Open"] = JobOrderKind.OpenCasket,
            ["TendPatient"] = JobOrderKind.Tend, ["Equip"] = JobOrderKind.Equip, ["Wear"] = JobOrderKind.Wear,
            ["Rescue"] = JobOrderKind.Rescue, ["Capture"] = JobOrderKind.Capture, ["Arrest"] = JobOrderKind.Arrest,
            ["AttackMelee"] = JobOrderKind.Subdue, ["FixBrokenDownBuilding"] = JobOrderKind.FixBreakdown,
            ["Refuel"] = JobOrderKind.Refuel, [HaulWaste] = JobOrderKind.Waste,
        };
        internal const string UseItem = "UseItem";
        // HaulWaste names the waste haul (NativeWasteOperations): the Hauling
        // giver's HaulToCell or burial job to a separated destination.
        internal const string HaulWaste = "HaulWaste";

        private static string? Id(Common.Ref? r) => r != null && r.HasId && ProtoBoundary.IsIdentifier(r.Id) ? r.Id : null;

        // The intent's shape, before any live check: which arm applies it.
        internal enum Arm { Invalid, Order, Need, Use, Prioritized }

        internal static Arm Classify(Operations.GiveJobIntent? intent, out JobOrder? order, out string? refusal)
        {
            order = null; refusal = null;
            var pawn = Id(intent?.Pawn);
            if (intent == null || pawn == null) { refusal = "Give job requires a pawn."; return Arm.Invalid; }
            var targets = intent.Targets.Select(Id).ToList();
            if (targets.Any(t => t == null || t == pawn)) { refusal = "Give job targets must be exact and distinct from the pawn."; return Arm.Invalid; }
            var options = intent.Options;
            if (options != null && options.HasRelieveNeed)
            {
                if (intent.HasJob || targets.Count > 0 || options.Prioritized || options.RelieveNeed == Operations.Need.Unspecified)
                { refusal = "Need relief names a need and no job or targets: the need giver picks them."; return Arm.Invalid; }
                return Arm.Need;
            }
            if (!intent.HasJob || !ProtoBoundary.IsIdentifier(intent.Job)) { refusal = "Give job requires a JobDef name."; return Arm.Invalid; }
            if (intent.Job == UseItem)
            {
                if (targets.Count != 2 || targets[0] == targets[1]) { refusal = "UseItem requires targets [item, target pawn]."; return Arm.Invalid; }
                return Arm.Use;
            }
            if (Kinds.TryGetValue(intent.Job, out var kind))
            {
                var arrest = kind == JobOrderKind.Arrest;
                if (targets.Count != (arrest ? 2 : 1)) { refusal = arrest ? "Arrest requires targets [target, prisoner bed]." : intent.Job + " requires one target."; return Arm.Invalid; }
                if (options != null && options.Unwanted && kind != JobOrderKind.Waste) { refusal = "Only HaulWaste takes unwanted."; return Arm.Invalid; }
                order = new JobOrder { Kind = kind, PawnId = pawn, TargetId = targets[0]!, BedId = arrest ? targets[1] : null, Unwanted = options?.Unwanted == true };
                return Arm.Order;
            }
            if (options != null && options.Prioritized)
            {
                if (targets.Count != 1) { refusal = "A prioritized job requires one target."; return Arm.Invalid; }
                return Arm.Prioritized;
            }
            refusal = "Job " + intent.Job + " is not a GiveJobIntent job without prioritized.";
            return Arm.Invalid;
        }

        // The spawned pawn the order names and its live control snapshot.
        internal static Common.Failure? Pawn(JobOrder intent, Common.ObservationContext context, out Pawn? pawn, out NativePawnSnapshot? snapshot)
        {
            pawn = null; snapshot = null;
            if (!NativePawnControlState.IsReady) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            var map = ProtoBoundary.LoadedMap(context);
            var identity = new NativeControlIdentity(Current.Game, map, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = map.mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (pawn == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact pawn is not spawned on this map.");
            var control = NativePawnControlState.Observe(identity, pawn, out snapshot);
            if (control != NativePawnControlResult.Ready || snapshot == null) return NativeDraftProtocol.Failure(control, context);
            return null;
        }

        internal static Receipts.EffectEvidence Evidence(Pawn pawn, Thing target, Job job, bool issued) => new Receipts.EffectEvidence
        {
            Job = new Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobId = job.loadID, JobDef = job.def?.defName ?? "",
                TargetA = new Receipts.JobTarget { ThingId = target.GetUniqueLoadID() },
                CanTry = true, Issued = issued, Verified = true, Drafted = pawn.Drafted,
            }
        };

        internal static Common.Failure? ValidateOrder(JobOrder order, Common.ObservationContext context)
        {
            switch (order.Kind)
            {
                case JobOrderKind.Repair: return NativeRepairOperations.Validate(order, context);
                case JobOrderKind.Clean: return NativeCleanOperations.Validate(order, context);
                case JobOrderKind.OpenCasket: return NativeOpenCasketOperations.Validate(order, context);
                case JobOrderKind.Tend: return NativeTendOperations.Validate(order, context);
                case JobOrderKind.Equip: return NativeEquipOperations.Validate(order, context);
                case JobOrderKind.Wear: return NativeGearOperations.Validate(order, context);
                case JobOrderKind.Rescue: case JobOrderKind.Capture: return NativeCustodyOperations.Validate(order, context);
                case JobOrderKind.Arrest: return NativeArrestOperations.Validate(order, context);
                case JobOrderKind.Subdue: return NativeSubdueOperations.Validate(order, context);
                case JobOrderKind.FixBreakdown: case JobOrderKind.Refuel: return NativeRecoveryOperations.Validate(order, context);
                case JobOrderKind.Waste: return NativeWasteOperations.Validate(order, context);
                default: return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Unsupported job order.");
            }
        }

        internal static Receipts.EffectEvidence ApplyOrder(JobOrder order, Common.ObservationContext context)
        {
            switch (order.Kind)
            {
                case JobOrderKind.Repair: return NativeRepairOperations.Apply(order, context);
                case JobOrderKind.Clean: return NativeCleanOperations.Apply(order, context);
                case JobOrderKind.OpenCasket: return NativeOpenCasketOperations.Apply(order, context);
                case JobOrderKind.Tend: return NativeTendOperations.Apply(order, context);
                case JobOrderKind.Equip: return NativeEquipOperations.Apply(order, context);
                case JobOrderKind.Wear: return NativeGearOperations.Apply(order, context);
                case JobOrderKind.Rescue: case JobOrderKind.Capture: return NativeCustodyOperations.Apply(order, context);
                case JobOrderKind.Arrest: return NativeArrestOperations.Apply(order, context);
                case JobOrderKind.Subdue: return NativeSubdueOperations.Apply(order, context);
                case JobOrderKind.FixBreakdown: case JobOrderKind.Refuel: return NativeRecoveryOperations.Apply(order, context);
                case JobOrderKind.Waste: return NativeWasteOperations.Apply(order, context);
                default: throw new InvalidOperationException("Unsupported job order.");
            }
        }
    }

    // options.prioritized (#1352): the vanilla "Prioritize" float-menu order
    // for any job a work giver builds on one thing, such as FinishFrame,
    // HaulToContainer to a blueprint or frame, or Deconstruct. The first work
    // giver whose work type the pawn may do and is assigned that builds
    // exactly the named JobDef on the target with forced set builds it; the
    // pawn takes it through TryTakeOrderedJobPrioritizedWork, player-forced.
    internal static class NativePrioritizedJob
    {
        private static Common.Failure? Resolve(Operations.GiveJobIntent intent, Common.ObservationContext context, out Pawn? pawn, out Thing? target, out JobDef? def, out bool running)
        {
            pawn = null; target = null; running = false;
            def = DefDatabase<JobDef>.GetNamedSilentFail(intent.Job);
            if (def == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Job " + intent.Job + " is not a JobDef.");
            var map = ProtoBoundary.LoadedMap(context);
            pawn = map.mapPawns.FreeColonistsSpawned.ById(intent.Pawn.Id);
            if (pawn == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact colonist is not spawned on this map.");
            if (pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState || !pawn.IsColonistPlayerControlled)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Colonist is unavailable, drafted or in a mental state.");
            target = RefIndex.Thing(map, intent.Targets[0].Id);
            if (target == null || !target.Spawned) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact target is not spawned on this map.");
            var job = pawn.CurJob;
            running = job != null && job.def == def && job.targetA.Thing == target;
            return null;
        }

        private static WorkGiverJobResult? Build(Pawn pawn, Thing target, JobDef def, out string? reason)
        {
            var p = pawn;
            return WorkGiverDispatch.TryJob(pawn, target,
                g => g.workType != null && !p.WorkTypeIsDisabled(g.workType) && p.workSettings != null && p.workSettings.WorkIsActive(g.workType),
                out reason, def);
        }

        internal static Common.Failure? Validate(Operations.GiveJobIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var target, out var def, out var running);
            if (failure != null || running) return failure;
            if (Build(pawn!, target!, def!, out var reason) == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No work giver the colonist may do builds " + def!.defName + " on the target" + (reason != null ? ": " + reason : "."));
            return null;
        }

        internal static Receipts.EffectEvidence Apply(Operations.GiveJobIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var target, out var def, out var running);
            if (failure != null) throw new ApplyRefusedException(failure.Code, failure.Detail);
            if (running) return NativeGiveJob.Evidence(pawn!, target!, pawn!.CurJob, false);
            var result = Build(pawn!, target!, def!, out var reason);
            if (result == null) throw new InvalidOperationException("No work giver builds " + def!.defName + " on the target" + (reason != null ? ": " + reason : "."));
            result.Job.playerForced = true;
            if (!pawn!.jobs.TryTakeOrderedJobPrioritizedWork(result.Job, result.Scanner, target!.Position))
                throw new InvalidOperationException("The colonist did not take the prioritized job.");
            return NativeGiveJob.Evidence(pawn, target, result.Job, true);
        }
    }

    internal sealed class GiveJobActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.GiveJob;
            switch (NativeGiveJob.Classify(intent, out var order, out var refusal))
            {
                case NativeGiveJob.Arm.Order: return NativeGiveJob.ValidateOrder(order!, context);
                case NativeGiveJob.Arm.Need: return NativeMoodReliefOperations.Validate(intent.Pawn.Id, intent.Options.RelieveNeed, context);
                case NativeGiveJob.Arm.Use: return NativeUseItemOperations.Validate(intent.Pawn.Id, intent.Targets[0].Id, intent.Targets[1].Id, context);
                case NativeGiveJob.Arm.Prioritized: return NativePrioritizedJob.Validate(intent, context);
                default: return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refusal ?? "Invalid give job intent.");
            }
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.GiveJob;
            switch (NativeGiveJob.Classify(intent, out var order, out var refusal))
            {
                case NativeGiveJob.Arm.Order: return NativeGiveJob.ApplyOrder(order!, context);
                case NativeGiveJob.Arm.Need: return NativeMoodReliefOperations.Apply(intent.Pawn.Id, intent.Options.RelieveNeed, context);
                case NativeGiveJob.Arm.Use: return NativeUseItemOperations.Apply(intent.Pawn.Id, intent.Targets[0].Id, intent.Targets[1].Id, context);
                case NativeGiveJob.Arm.Prioritized: return NativePrioritizedJob.Apply(intent, context);
                default: throw new InvalidOperationException(refusal ?? "Invalid give job intent.");
            }
        }
    }
}
