#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Snapshots grant no authority. The controller separately retains the original
    // supply cohort; this boundary only permits Allow on an exact observed item.
    internal static class NativeSupplyAllow
    {
        internal static bool Valid(Operations.DesignateThing? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Target) && command.HasDesignation
            && command.Designation == Operations.ThingDesignation.Allow;

        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && thing.Map == Find.CurrentMap && thing.def.EverHaulable
            && thing.def.category == ThingCategory.Item && !thing.Position.Fogged(thing.Map)
            && Faction.OfPlayerSilentFail != null
            && (thing.Faction == null || thing.Faction == Faction.OfPlayerSilentFail)
            && thing.TryGetComp<CompForbiddable>() != null;

        internal static string Token(Common.Identity identity, string id, string definition,
            int x, int z, int count, bool forbidden, string faction)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(id); writer.Write(definition); writer.Write(x); writer.Write(z);
                    writer.Write(count); writer.Write(forbidden); writer.Write(faction);
                }
                using (var hash = SHA256.Create())
                    return "allow-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, thing.GetUniqueLoadID(), thing.def.defName,
                    thing.Position.x, thing.Position.z, thing.stackCount, thing.IsForbidden(Faction.OfPlayer),
                    thing.Faction?.GetUniqueLoadID() ?? "") };
        }

        private static bool Prepare(Operations.DesignateThing command, Common.ObservationContext context,
            out Thing? thing, out Common.Failure failure)
        {
            thing = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Allow requires an exact supply snapshot; other designations are unsupported.");
            if (command == null) return false;
            if (command.HasDesignation && command.Designation != Operations.ThingDesignation.Allow)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Only the Allow designation is implemented by this adapter."); return false; }
            if (!Valid(command)) return false;
            thing = Find.CurrentMap.listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Target.EntityId);
            if (thing == null || !Eligible(thing))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible loose supply is unavailable."); return false; }
            if (Snapshot(thing, context)?.Token != command.Target.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Supply snapshot changed; observe before new admission."); return false; }
            if (!thing.IsForbidden(Faction.OfPlayer) || !new Designator_Unforbid().CanDesignateThing(thing).Accepted)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Ordinary native Allow is not available for this item."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Thing thing) => new Receipts.EffectEvidence {
            Designation = new Receipts.DesignationEffect { ThingId = thing.GetUniqueLoadID(),
                DesignationDef = "Allow", Present = !thing.IsForbidden(Faction.OfPlayer),
                ResourceDef = thing.def.defName, Cell = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } } };

        internal static Operations.PreviewReply Preview(Operations.DesignateThing command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out var thing, out var failure)) return new Operations.PreviewReply { Failure = failure };
                // Proposed=true is never a claim that an effect already happened.
                var proposed = Evidence(thing!); proposed.Designation.Present = true;
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = proposed } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Supply preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Authority.Owner? owner = null;
            Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition;
            try
            {
                if (!Prepare(request.Operation.DesignateThing, context, out var thing, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                owner = new Authority.Owner { ControllerSessionId = guard.Snapshot.Lease!.ControllerSessionId, PlayerDirection = guard.Snapshot.Lease.PlayerDirection };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context, owner);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!;
                handle = admitted.Handle;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                    if (!current.Success || !Prepare(request.Operation.DesignateThing, context, out var checkedThing, out failure)
                        || !ReferenceEquals(checkedThing, thing)) throw new InvalidOperationException("Supply admission changed before effect.");
                    new Designator_Unforbid().DesignateThing(thing);
                    if (!Eligible(thing!) || thing!.IsForbidden(Faction.OfPlayer)) throw new InvalidOperationException("Native Allow did not produce an allowed item.");
                    evidence = Evidence(thing!);
                    state.AllowedSupplies.Add(pre.Attempt.Clone(), evidence.Designation.Clone());
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, owner, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Supply admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, owner!, evidence!, "Admitted Allow requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Receipts.DesignationEffect original)
        {
            try
            {
                var thing = Find.CurrentMap.listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == original.ThingId);
                return ObservedProgress(attempt, context, original, thing != null && Eligible(thing) ? Evidence(thing).Designation : null);
            }
            catch (Exception) { return ObservedProgress(attempt, context, original, null); }
        }

        internal static Receipts.Progress ObservedProgress(Common.AttemptKey attempt, Common.ObservationContext context,
            Receipts.DesignationEffect original, Receipts.DesignationEffect? observed)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            if (observed == null || !observed.HasPresent || observed.ThingId != original.ThingId
                || observed.ResourceDef != original.ResourceDef || observed.DesignationDef != "Allow")
                result.Unknown = new Receipts.UnknownEffect { Reason = "Exact admitted supply is no longer observable; absence does not prove Allow." };
            else
            {
                result.CompleteInspection = true;
                var evidence = new Receipts.EffectEvidence { Designation = observed.Clone() };
                if (observed.Present) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "The exact supply is forbidden again; do not override renewed player forbidding." };
            }
            return result;
        }
    }
}
