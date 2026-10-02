#nullable enable
using System.Linq;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The PawnOrderIntent arm of Actions/Apply (#939): one pawn, one target,
    // one order kind, each kind validated and applied by its own operations
    // class against live state.
    internal static class NativePawnOrderIntent
    {
        // The spawned pawn the intent names and its live control snapshot.
        internal static Common.Failure? Pawn(Operations.PawnOrderIntent? intent, Common.ObservationContext context, out Pawn? pawn, out NativePawnSnapshot? snapshot)
        {
            pawn = null; snapshot = null;
            if (intent == null || !intent.HasPawnId || !intent.HasTargetId || !ProtoBoundary.IsIdentifier(intent.PawnId) || !ProtoBoundary.IsIdentifier(intent.TargetId) || intent.PawnId == intent.TargetId)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A pawn order requires distinct pawn and target ids.");
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
    }

    internal sealed class PawnOrderActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.PawnOrder;
            switch (intent?.Kind)
            {
                case Operations.PawnOrderKind.Repair: return NativeRepairOperations.Validate(intent, context);
                case Operations.PawnOrderKind.Clean: return NativeCleanOperations.Validate(intent, context);
                case Operations.PawnOrderKind.OpenCasket: return NativeOpenCasketOperations.Validate(intent, context);
                case Operations.PawnOrderKind.Tend: return NativeTendOperations.Validate(intent, context);
                case Operations.PawnOrderKind.Equip: return NativeEquipOperations.Validate(intent, context);
                case Operations.PawnOrderKind.Wear: return NativeGearOperations.Validate(intent, context);
                case Operations.PawnOrderKind.Rescue: case Operations.PawnOrderKind.Capture: return NativeCustodyOperations.Validate(intent, context);
                case Operations.PawnOrderKind.Arrest: return NativeArrestOperations.Validate(intent, context);
                case Operations.PawnOrderKind.Subdue: return NativeSubdueOperations.Validate(intent, context);
                default: return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This pawn order kind is not an Actions/Apply intent.");
            }
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.PawnOrder;
            switch (intent.Kind)
            {
                case Operations.PawnOrderKind.Repair: return NativeRepairOperations.Apply(intent, context);
                case Operations.PawnOrderKind.Clean: return NativeCleanOperations.Apply(intent, context);
                case Operations.PawnOrderKind.OpenCasket: return NativeOpenCasketOperations.Apply(intent, context);
                case Operations.PawnOrderKind.Tend: return NativeTendOperations.Apply(intent, context);
                case Operations.PawnOrderKind.Equip: return NativeEquipOperations.Apply(intent, context);
                case Operations.PawnOrderKind.Wear: return NativeGearOperations.Apply(intent, context);
                case Operations.PawnOrderKind.Rescue: case Operations.PawnOrderKind.Capture: return NativeCustodyOperations.Apply(intent, context);
                case Operations.PawnOrderKind.Arrest: return NativeArrestOperations.Apply(intent, context);
                case Operations.PawnOrderKind.Subdue: return NativeSubdueOperations.Apply(intent, context);
                default: throw new System.InvalidOperationException("Unsupported pawn order kind.");
            }
        }
    }
}
