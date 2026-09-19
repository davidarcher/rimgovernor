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
        internal const string Kind = "Allow";
        internal static bool Valid(Operations.DesignateThing? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Target) && command.HasDesignation
            && (command.Designation == Operations.ThingDesignation.Allow || command.Designation == Operations.ThingDesignation.Forbid);

        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && ProtoBoundary.IsLoaded(thing.Map) && thing.def.EverHaulable
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
            if (command.HasDesignation && command.Designation != Operations.ThingDesignation.Allow && command.Designation != Operations.ThingDesignation.Forbid)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Only Allow and Forbid are implemented by this adapter."); return false; }
            if (!Valid(command)) return false;
            var forbid = command.Designation == Operations.ThingDesignation.Forbid;
            Designator designator = forbid ? (Designator)new Designator_Forbid() : new Designator_Unforbid();
            var found = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == command.Target.EntityId);
            // The apply-time precondition list for Allow
            // (action-contracts.md): Eligible one rule at a time, then the
            // designator's own answer, then the token.
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact item is no longer spawned on this map")
                .Require(() => found!.def.EverHaulable && found.def.category == ThingCategory.Item && found.TryGetComp<CompForbiddable>() != null, "the item is not a forbiddable haulable item")
                .Require(() => !found!.Position.Fogged(found.Map), "the item's cell is fogged")
                .Require(() => Faction.OfPlayerSilentFail != null && (found!.Faction == null || found.Faction == Faction.OfPlayerSilentFail), "the item belongs to another faction")
                .Require(() => found!.IsForbidden(Faction.OfPlayer) != forbid, "the item already has the desired forbid state")
                .Require(() => EventLootFacts.Safe(found!) == !forbid, "hauling safety no longer permits this forbid state")
                .Require(() => designator.CanDesignateThing(found!).Accepted, "the native unforbid designator refuses the item")
                .Token(() => Snapshot(found!, context)?.Token == command.Target.ExpectedSnapshotToken, "the item snapshot changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            thing = found;
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Thing thing, bool forbid = false) => new Receipts.EffectEvidence {
            Designation = new Receipts.DesignationEffect { ThingId = thing.GetUniqueLoadID(),
                DesignationDef = forbid ? "Forbid" : "Allow", Present = thing.IsForbidden(Faction.OfPlayer) == forbid,
                ResourceDef = thing.def.defName, Cell = new Common.Cell { X = thing.Position.x, Z = thing.Position.z } } };

        internal static Operations.PreviewReply Preview(Operations.DesignateThing command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out var thing, out var failure)) return new Operations.PreviewReply { Failure = failure };
                // Proposed=true is never a claim that an effect already happened.
                var proposed = Evidence(thing!, command.Designation == Operations.ThingDesignation.Forbid); proposed.Designation.Present = true;
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = proposed } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Supply preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition;
            try
            {
                if (!Prepare(request.Operation.DesignateThing, context, out var thing, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success || !Prepare(request.Operation.DesignateThing, context, out var checkedThing, out failure)
                        || !ReferenceEquals(checkedThing, thing)) throw new InvalidOperationException("Supply admission changed before effect.");
                    var forbid = request.Operation.DesignateThing.Designation == Operations.ThingDesignation.Forbid;
                    Designator designator = forbid ? (Designator)new Designator_Forbid() : new Designator_Unforbid();
                    designator.DesignateThing(thing);
                    if (!Eligible(thing!) || thing!.IsForbidden(Faction.OfPlayer) != forbid) throw new InvalidOperationException("Native supply designation did not achieve its forbid state.");
                    evidence = Evidence(thing!, forbid);
                    state.AllowedSupplies.Add(pre.Attempt.Clone(), evidence.Designation.Clone());
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Supply admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted Allow requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Receipts.DesignationEffect original)
        {
            try
            {
                var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == original.ThingId);
                return ObservedProgress(attempt, context, original, thing != null && Eligible(thing) ? Evidence(thing, original.DesignationDef == "Forbid").Designation : null);
            }
            catch (Exception) { return ObservedProgress(attempt, context, original, null); }
        }

        internal static Receipts.Progress ObservedProgress(Common.AttemptKey attempt, Common.ObservationContext context,
            Receipts.DesignationEffect original, Receipts.DesignationEffect? observed)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            if (observed == null || !observed.HasPresent || observed.ThingId != original.ThingId
                || observed.ResourceDef != original.ResourceDef || observed.DesignationDef != original.DesignationDef)
            {
                // The exact item leaving the loose census is the ordinary
                // consequence of a successful Allow (a colonist ate, carried,
                // merged or hauled it), and Execute recorded this evidence
                // only after verifying the item allowed inside the owned
                // authority. That record completes the attempt; holding it
                // Unknown kept a plan open forever and starved the remaining
                // starting supplies of any further Allow (#114).
                if (original != null && original.HasPresent && original.Present && (original.DesignationDef == "Allow" || original.DesignationDef == "Forbid"))
                {
                    result.CompleteInspection = true;
                    result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Designation = original.Clone() } };
                }
                else result.Unknown = new Receipts.UnknownEffect { Reason = "Exact admitted supply is no longer observable; absence does not prove Allow." };
            }
            else
            {
                result.CompleteInspection = true;
                var evidence = new Receipts.EffectEvidence { Designation = observed.Clone() };
                if (observed.Present) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "The exact supply no longer has the requested forbid state." };
            }
            return result;
        }
    }
}
