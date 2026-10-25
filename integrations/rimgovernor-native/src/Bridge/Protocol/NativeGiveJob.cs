using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // The single-target jobs a GiveJobIntent names, each validated and built
    // by its own operations class against live state.
    internal enum JobOrderKind { Repair, Tend, Equip, DropWeapon, Wear, Rescue, Capture, Arrest, Subdue, FixBreakdown, Refuel, InvestigateMonolith, ActivateMonolith, InteractThing, InspectGravEngine, OfferHelp }

    // One pawn, one target (and Arrest's bed) for a JobOrderKind.
    internal sealed class JobOrder
    {
        internal JobOrderKind Kind;
        internal string PawnId = "", TargetId = "";
        internal string? BedId;
        internal bool HasBedId => BedId != null;
    }

    // The GiveJobIntent arm of Actions/Apply: one pawn, one vanilla
    // job, its targets in job target order. The brain chose the pawn and the
    // targets; native checks the game rules live and builds the game's job.
    internal static class NativeGiveJob
    {
        private static readonly Dictionary<string, JobOrderKind> Kinds = new Dictionary<string, JobOrderKind>
        {
            ["Repair"] = JobOrderKind.Repair,
            ["TendPatient"] = JobOrderKind.Tend, ["Equip"] = JobOrderKind.Equip, [DropWeapon] = JobOrderKind.DropWeapon, ["Wear"] = JobOrderKind.Wear,
            ["Rescue"] = JobOrderKind.Rescue, ["Capture"] = JobOrderKind.Capture, ["Arrest"] = JobOrderKind.Arrest,
            ["AttackMelee"] = JobOrderKind.Subdue, ["FixBrokenDownBuilding"] = JobOrderKind.FixBreakdown,
            ["Refuel"] = JobOrderKind.Refuel,
            ["OfferHelp"] = JobOrderKind.OfferHelp,
            ["InvestigateMonolith"] = JobOrderKind.InvestigateMonolith, ["ActivateMonolith"] = JobOrderKind.ActivateMonolith, ["InteractThing"] = JobOrderKind.InteractThing, ["InspectGravEngine"] = JobOrderKind.InspectGravEngine,
        };
        internal const string UseItem = "UseItem";
        // DropWeapon names the drop of the weapon in the pawn's hands
        // (NativeDropOperations): the job whose driver is JobDriver_DropEquipment.
        internal const string DropWeapon = "DropWeapon";

        private static string? Id(RimGovernor.Brain.GameFacts.Common.Ref? r) => r != null && r.Id is not null && GameThread.IsIdentifier(r.Id) ? (r.Id ?? "") : null;

        // The intent's shape, before any live check: which arm applies it.
        internal enum Arm { Invalid, Order, Use, Prioritized }

        internal static Arm Classify(RimGovernor.Brain.GameOrders.Operations.GiveJobIntent? intent, out JobOrder? order, out string? refusal)
        {
            order = null; refusal = null;
            var pawn = Id(intent?.Pawn);
            if (intent == null || pawn == null) { refusal = "Give job requires a pawn."; return Arm.Invalid; }
            var targets = intent.Targets.Select(Id).ToList();
            // UseItem alone may target its own user: [item, pawn] is the self-use of a usable item.
            var selfOk = (intent.Job ?? "") == UseItem;
            if (targets.Any(t => t == null || t == pawn && !selfOk)) { refusal = "Give job targets must be exact and distinct from the pawn."; return Arm.Invalid; }
            var options = intent.Options;
            if (intent.Job is null || !GameThread.IsIdentifier(intent.Job)) { refusal = "Give job requires a JobDef name."; return Arm.Invalid; }
            if ((intent.Job ?? "") == UseItem)
            {
                if (targets.Count != 2 || targets[0] == targets[1] || targets[0] == pawn) { refusal = "UseItem requires targets [item, target pawn]."; return Arm.Invalid; }
                return Arm.Use;
            }
            if (Kinds.TryGetValue((intent.Job ?? ""), out var kind))
            {
                var arrest = kind == JobOrderKind.Arrest;
                if (targets.Count != (arrest ? 2 : 1)) { refusal = arrest ? "Arrest requires targets [target, prisoner bed]." : (intent.Job ?? "") + " requires one target."; return Arm.Invalid; }
                order = new JobOrder { Kind = kind, PawnId = pawn, TargetId = (targets[0] ?? throw new InvalidOperationException("The job target is unavailable.")), BedId = arrest ? targets[1] : null };
                return Arm.Order;
            }
            if (options != null && options.Prioritized)
            {
                if (targets.Count != 1) { refusal = "A prioritized job requires one target."; return Arm.Invalid; }
                return Arm.Prioritized;
            }
            refusal = "Job " + (intent.Job ?? "") + " is not a GiveJobIntent job without prioritized.";
            return Arm.Invalid;
        }

        // The spawned pawn the order names and its live control snapshot.
        internal static bool TryPawn(JobOrder intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Pawn? pawn,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NativePawnSnapshot? snapshot,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out RimGovernor.Brain.GameFacts.Common.Failure? failure)
        {
            pawn = null; snapshot = null; failure = null;
            if (!NativePawnControlState.IsReady)
            {
                failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
                return false;
            }
            var map = GameThread.LoadedMap(context);
            var identity = new NativeControlIdentity(Current.Game, map, (context.Identity?.ColonyId ?? ""), (context.Identity?.LoadToken ?? ""));
            pawn = map.mapPawns.AllPawnsSpawned.ById(intent.PawnId);
            if (pawn == null)
            {
                failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact pawn is not spawned on this map.");
                return false;
            }
            var control = NativePawnControlState.Observe(identity, pawn, out snapshot);
            if (control != NativePawnControlResult.Ready || snapshot == null)
            {
                failure = NativeDraftProtocol.Failure(control, context);
                return false;
            }
            return true;
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence(Pawn pawn, Thing target, Job job, bool issued) => new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence
        {
            Effect = new RimGovernor.Brain.GameFacts.Receipts.JobEffect
            {
                PawnId = pawn.GetUniqueLoadID(), JobDef = job.def?.defName ?? "",
                TargetA = new RimGovernor.Brain.GameFacts.Receipts.JobTarget { Target = new RimGovernor.Brain.GameFacts.Receipts.JobTargetTarget.ThingIdCase(target.GetUniqueLoadID()) },
                Issued = issued,
            }
        };

        internal static RimGovernor.Brain.GameFacts.Common.Failure? ValidateOrder(JobOrder order, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            switch (order.Kind)
            {
                case JobOrderKind.Repair: return NativeRepairOperations.Validate(order, context);
                case JobOrderKind.Tend: return NativeTendOperations.Validate(order, context);
                case JobOrderKind.Equip: return NativeEquipOperations.Validate(order, context);
                case JobOrderKind.DropWeapon: return NativeDropOperations.Validate(order, context);
                case JobOrderKind.Wear: return NativeGearOperations.Validate(order, context);
                case JobOrderKind.Rescue: case JobOrderKind.Capture: return NativeCustodyOperations.Validate(order, context);
                case JobOrderKind.Arrest: return NativeArrestOperations.Validate(order, context);
                case JobOrderKind.Subdue: return NativeSubdueOperations.Validate(order, context);
                case JobOrderKind.FixBreakdown: case JobOrderKind.Refuel: return NativeRecoveryOperations.Validate(order, context);
                case JobOrderKind.InvestigateMonolith: case JobOrderKind.ActivateMonolith: return NativeMonolithOperations.Validate(order, context);
                case JobOrderKind.InteractThing: return NativeInteractOperations.Validate(order, context);
                case JobOrderKind.InspectGravEngine: return NativeGravEngineInspectionOperations.Validate(order, context);
                case JobOrderKind.OfferHelp: return NativeQuestJoinerOperations.Validate(order, context);
                default: return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unsupported, "Unsupported job order.");
            }
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence ApplyOrder(JobOrder order, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            switch (order.Kind)
            {
                case JobOrderKind.Repair: return NativeRepairOperations.Apply(order, context);
                case JobOrderKind.Tend: return NativeTendOperations.Apply(order, context);
                case JobOrderKind.Equip: return NativeEquipOperations.Apply(order, context);
                case JobOrderKind.DropWeapon: return NativeDropOperations.Apply(order, context);
                case JobOrderKind.Wear: return NativeGearOperations.Apply(order, context);
                case JobOrderKind.Rescue: case JobOrderKind.Capture: return NativeCustodyOperations.Apply(order, context);
                case JobOrderKind.Arrest: return NativeArrestOperations.Apply(order, context);
                case JobOrderKind.Subdue: return NativeSubdueOperations.Apply(order, context);
                case JobOrderKind.FixBreakdown: case JobOrderKind.Refuel: return NativeRecoveryOperations.Apply(order, context);
                case JobOrderKind.InvestigateMonolith: case JobOrderKind.ActivateMonolith: return NativeMonolithOperations.Apply(order, context);
                case JobOrderKind.InteractThing: return NativeInteractOperations.Apply(order, context);
                case JobOrderKind.InspectGravEngine: return NativeGravEngineInspectionOperations.Apply(order, context);
                case JobOrderKind.OfferHelp: return NativeQuestJoinerOperations.Apply(order, context);
                default: throw new InvalidOperationException("Unsupported job order.");
            }
        }
    }

    // options.prioritized: the vanilla "Prioritize" float-menu order
    // for any job a work giver builds on one thing, such as FinishFrame,
    // HaulToContainer to a blueprint or frame, or Deconstruct. The first work
    // giver whose work type the pawn may do and is assigned that builds
    // exactly the named JobDef on the target with forced set builds it; the
    // pawn takes it through TryTakeOrderedJobPrioritizedWork, player-forced.
    internal static class NativePrioritizedJob
    {
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.GiveJobIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Pawn? pawn, out Thing? target, out JobDef? def, out bool running)
        {
            pawn = null; target = null; running = false;
            def = DefDatabase<JobDef>.GetNamedSilentFail((intent.Job ?? ""));
            if (def == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Job " + (intent.Job ?? "") + " is not a JobDef.");
            var map = GameThread.LoadedMap(context);
            pawn = map.mapPawns.FreeColonistsSpawned.ById(intent.Pawn?.Id ?? "");
            if (pawn == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact colonist is not spawned on this map.");
            if (pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState || !pawn.IsColonistPlayerControlled)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Colonist is unavailable, drafted or in a mental state.");
            target = RefIndex.Thing(map, intent.Targets[0].Id ?? "");
            if (target == null || !target.Spawned) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact target is not spawned on this map.");
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

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.GiveJobIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var target, out var def, out var running);
            if (failure != null || running) return failure;
            if (pawn == null || target == null || def == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NativeFailure, "Prioritized job prerequisites are unavailable.");
            if (Build(pawn, target, def, out var reason) == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "No work giver the colonist may do builds " + def.defName + " on the target" + (reason != null ? ": " + reason : "."));
            return null;
        }

        // The job the work giver builds on the target, taken as a player-forced order. Null
        // (reason set) when no giver builds it or the pawn does not take it.
        internal static Job? TryTake(Pawn pawn, Thing target, JobDef def, out string? reason)
        {
            var result = Build(pawn, target, def, out reason);
            if (result == null) { reason = "No work giver builds " + def.defName + " on the target" + (reason != null ? ": " + reason : "."); return null; }
            result.Job.playerForced = true;
            if (!pawn.jobs.TryTakeOrderedJobPrioritizedWork(result.Job, result.Scanner, target.Position)) { reason = "The colonist did not take the prioritized job."; return null; }
            return result.Job;
        }

        // Whether a work giver the pawn may do builds the job on the target (nothing is taken).
        internal static bool CanTake(Pawn pawn, Thing target, JobDef def) => Build(pawn, target, def, out _) != null;

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.GiveJobIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var pawn, out var target, out var def, out var running);
            if (failure != null) throw new ApplyRefusedException(failure);
            if (pawn == null || target == null || def == null) throw new InvalidOperationException("Prioritized job prerequisites are unavailable.");
            if (running) return NativeGiveJob.Evidence(pawn, target, pawn.CurJob, false);
            var job = TryTake(pawn, target, def, out var reason);
            if (job == null) throw new InvalidOperationException(reason);
            return NativeGiveJob.Evidence(pawn, target, job, true);
        }
    }

    internal sealed class GiveJobActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.GiveJob;
            switch (NativeGiveJob.Classify(intent, out var order, out var refusal))
            {
                case NativeGiveJob.Arm.Order: return NativeGiveJob.ValidateOrder((order ?? throw new System.InvalidOperationException("Required native value is unavailable: order.")), context);
                case NativeGiveJob.Arm.Use: return NativeUseItemOperations.Validate(((intent ?? throw new System.InvalidOperationException("Required native value is unavailable: intent.")).Pawn ?? throw new System.InvalidOperationException("Required native value is unavailable: (intent ?? throw new System.InvalidOperationException(\"Required native value is unavailable: intent.\")).Pawn.")).Id ?? "", intent.Targets[0].Id ?? "", intent.Targets[1].Id ?? "", context);
                case NativeGiveJob.Arm.Prioritized: return NativePrioritizedJob.Validate((intent ?? throw new System.InvalidOperationException("Required native value is unavailable: intent.")), context);
                default: return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, refusal ?? "Invalid give job intent.");
            }
        }

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.GiveJob;
            switch (NativeGiveJob.Classify(intent, out var order, out var refusal))
            {
                case NativeGiveJob.Arm.Order: return NativeGiveJob.ApplyOrder((order ?? throw new System.InvalidOperationException("Required native value is unavailable: order.")), context);
                case NativeGiveJob.Arm.Use: return NativeUseItemOperations.Apply(((intent ?? throw new System.InvalidOperationException("Required native value is unavailable: intent.")).Pawn ?? throw new System.InvalidOperationException("Required native value is unavailable: (intent ?? throw new System.InvalidOperationException(\"Required native value is unavailable: intent.\")).Pawn.")).Id ?? "", intent.Targets[0].Id ?? "", intent.Targets[1].Id ?? "", context);
                case NativeGiveJob.Arm.Prioritized: return NativePrioritizedJob.Apply((intent ?? throw new System.InvalidOperationException("Required native value is unavailable: intent.")), context);
                default: throw new InvalidOperationException(refusal ?? "Invalid give job intent.");
            }
        }
    }
}
