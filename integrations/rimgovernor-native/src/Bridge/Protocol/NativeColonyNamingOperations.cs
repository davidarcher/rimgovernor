#nullable enable
using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Tracks an admitted ConfirmColonyNames attempt so a later ObserveProgress
    // poll can re-verify it purely from live native state, exactly like every
    // other Native*Record. Settlement is held directly, the same way other
    // records hold live Thing references (e.g. NativeConstructionTracking).
    internal sealed class NativeNamingRecord
    {
        internal readonly Settlement Settlement;
        internal readonly string FactionName;
        internal readonly string SettlementName;
        internal readonly int WindowId;
        internal NativeNamingRecord(Settlement settlement, string factionName, string settlementName, int windowId)
        { Settlement = settlement; FactionName = factionName; SettlementName = settlementName; WindowId = windowId; }
    }

    // Typed ConfirmColonyNames: the colony-wide, one-shot autopilot-eligible
    // counterpart of the legacy, unauthenticated home/confirm_colony_names tool
    // (ColonyNamingTool.cs), reusing its exact dialog lookup, field reflection
    // and native name validators/callbacks, but admitted through the same
    // attempt/lease/ledger machinery every other rimgovernor/operations_execute
    // command requires. This is the autopilot-eligible typed surface decided in
    // presentation-coverage.md; it is independent of PlayerPresentation.Apply's
    // own confirm_colony_names branch, which stays explicit-player-only and
    // unregistered.
    internal static class NativeColonyNamingOperations
    {
        private static string? Field(Dialog_GiveName dialog, string name) =>
            (AccessTools.Field(typeof(Dialog_GiveName), name)?.GetValue(dialog) as string)?.Trim();

        // Stale means the observed window/suggestions no longer match live state;
        // that is a precondition failure, not a native-validation refusal.
        private static bool PrepareStale(Operations.ConfirmColonyNames? command,
            out Dialog_NamePlayerFactionAndSettlement dialog, out Settlement settlement)
        {
            dialog = null!; settlement = null!;
            if (command == null || !command.HasWindowId || !command.HasFactionName || !command.HasSettlementName) return false;
            var pending = ColonyNamingTools.Pending();
            if (pending == null || pending.ID != command.WindowId) return false;
            if (Field(pending, "curName") != command.FactionName || Field(pending, "curSecondName") != command.SettlementName) return false;
            var target = AccessTools.Field(typeof(Dialog_NamePlayerFactionAndSettlement), "settlement")?.GetValue(pending) as Settlement;
            if (target == null || !ProtoBoundary.IsLoaded(target.Map)) return false;
            dialog = pending; settlement = target;
            return true;
        }

        private static bool ValidatesNatively(Dialog_NamePlayerFactionAndSettlement dialog, Operations.ConfirmColonyNames command)
        {
            var type = typeof(Dialog_NamePlayerFactionAndSettlement);
            return (bool)AccessTools.Method(type, "IsValidName").Invoke(dialog, new object[] { command.FactionName })
                && (bool)AccessTools.Method(type, "IsValidSecondName").Invoke(dialog, new object[] { command.SettlementName });
        }

        internal static Operations.PreviewReply Preview(Operations.ConfirmColonyNames command, Common.ObservationContext context)
        {
            try
            {
                if (!PrepareStale(command, out var dialog, out _))
                    return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                        "Naming window or suggestions changed; inspect again.") };
                var accepted = ValidatesNatively(dialog, command);
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = accepted,
                    Reason = accepted ? "" : "Native naming validation refused the suggestions." } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Naming preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.ConfirmColonyNames;
            try
            {
                if (!PrepareStale(command, out var dialog, out var settlement))
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                        "Naming window or suggestions changed; inspect again.") };
                if (!ValidatesNatively(dialog, command))
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                        "Native naming validation refused the suggestions.") };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!;
                handle = admitted.Handle;
                state.Naming.Add(pre.Attempt.Clone(), new NativeNamingRecord(settlement, command.FactionName, command.SettlementName, command.WindowId));
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !PrepareStale(command, out var checkedDialog, out var checkedSettlement) || !ValidatesNatively(checkedDialog, command))
                        throw new InvalidOperationException("Naming admission changed before effect.");
                    var type = typeof(Dialog_NamePlayerFactionAndSettlement);
                    AccessTools.Method(type, "Named").Invoke(checkedDialog, new object[] { command.FactionName });
                    AccessTools.Method(type, "NamedSecond").Invoke(checkedDialog, new object[] { command.SettlementName });
                    Messages.Message("PlayerFactionAndBaseGainsName".Translate(command.FactionName, command.SettlementName),
                        MessageTypeDefOf.TaskCompletion, historical: false);
                    Find.WindowStack.TryRemove(checkedDialog);
                    var confirmed = Faction.OfPlayer.Name == command.FactionName && checkedSettlement.Name == command.SettlementName
                        && !Find.WindowStack.Windows.Contains(checkedDialog);
                    evidence = new Receipts.EffectEvidence { Naming = new Receipts.NamingEffect {
                        WindowId = command.WindowId, FactionName = Faction.OfPlayer.Name, SettlementName = checkedSettlement.Name, Confirmed = confirmed } };
                    if (!confirmed) throw new InvalidOperationException("Naming confirmation did not verify after native callbacks.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Naming admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted naming requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeNamingRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact naming state is unavailable." } };
            try
            {
                var factionName = Faction.OfPlayer?.Name;
                var settlementName = record.Settlement?.Name;
                var confirmed = factionName == record.FactionName && settlementName == record.SettlementName;
                var evidence = new Receipts.EffectEvidence { Naming = new Receipts.NamingEffect {
                    WindowId = record.WindowId, FactionName = factionName ?? "", SettlementName = settlementName ?? "", Confirmed = confirmed } };
                result.CompleteInspection = true;
                if (confirmed) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = "Faction/settlement name no longer matches the admitted confirmation." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
