#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for the quest-fulfillment direct-write order:
    // FulfillQuest. Ports the legacy home/fulfill_quest tool's
    // (QuestFulfillmentTool.cs, QuestFulfillmentTools.Run) native mechanics --
    // an actual TradeRequestComp caravan gizmo callback (Command_Action.
    // action()) and its native confirmation dialog (Dialog_MessageBox.
    // buttonAAction()) -- behind the typed boundary, invoked directly with no
    // UI or camera dependency. Unlike AcceptQuest this is not a plain settings
    // write: native drives the same job the legacy tool drove, so Execute
    // still resolves synchronously (there is no further native job to
    // observe), but the actual mechanism is comparable in shape to
    // NativeSettlementGiftOperations' trade-session dispatch.
    //
    // Two CAS tokens, both self-computed, matching bridge/quest_fulfill.go
    // exactly:
    //   quest.expected_snapshot_token -- NativeQuestOperations.Token(quest),
    //     the same token AcceptQuest already uses and
    //     NativeWorldProgressionObservation.Quests() already reports, so no
    //     dedicated candidate read is needed for it.
    //   caravan.expected_snapshot_token -- CaravanToken(id, tile, moving,
    //     sorted crew), reproducible by Go from ReadWorldProgression alone,
    //     using a distinct "caravan-fulfill-" prefix from SettlementGift's own
    //     "caravan-gift-" caravan token so a stale write intended for one
    //     family can never be admitted as the other.
    // expected_pawn_ids is the exact crew snapshot (mirrors the legacy tool's
    // own membership check) independent of either token. Neither token
    // encodes the requested resource/count: native alone re-derives and
    // re-checks those against the live TradeRequestComp immediately before
    // dispatch, exactly as the legacy tool did.
    internal sealed class NativeQuestFulfillRecord
    {
        internal readonly string QuestId;
        internal NativeQuestFulfillRecord(string questId) { QuestId = questId; }
    }

    internal static class NativeQuestFulfillOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Exact port of Go's questFulfillCaravanToken (bridge/quest_fulfill.go):
        // both sides hash the same already-observed CaravanState fields, so Go
        // never needs a dedicated candidate read to learn this value. Booleans
        // render as literal "true"/"false" (not .ToString()'s "True"/"False")
        // so Go's %t formatting produces an identical string.
        internal static string CaravanToken(string caravanId, int tile, bool moving, System.Collections.Generic.IEnumerable<string> pawnIds) =>
            "caravan-fulfill-" + Hash(caravanId + "|" + tile + "|" + (moving ? "true" : "false") + "|" + string.Join(",", pawnIds.OrderBy(id => id, StringComparer.Ordinal)));

        private static bool ValidCommand(Operations.FulfillQuest? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Quest) && NativeDraftProtocol.ValidEntity(command.Caravan)
            && command.ExpectedPawnIds.Count > 0 && command.ExpectedPawnIds.Count <= 64
            && command.ExpectedPawnIds.All(ProtoBoundary.IsIdentifier)
            && command.ExpectedPawnIds.Distinct().Count() == command.ExpectedPawnIds.Count;

        // Re-validates everything a stale read could have gotten wrong: exact
        // quest identity/CAS token and ongoing state, exact caravan
        // identity/membership/position token, that the quest has exactly one
        // native settlement trade objective and the caravan currently visits
        // that exact nonhostile settlement, that the live TradeRequestComp
        // still matches the quest part's requested resource/count, and that
        // native fulfillment is not disabled. Mirrors legacy
        // QuestFulfillmentTools.Run's own checks.
        private static bool Prepare(Operations.FulfillQuest command, out Quest? quest, out Caravan? caravan, out TradeRequestComp? request, out Command_Action? command2, out Common.Failure failure)
        {
            quest = null; caravan = null; request = null; command2 = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Quest fulfillment requires an exact current quest/caravan snapshot.");
            if (!ValidCommand(command)) return false;
            if (Find.TickManager == null || !Find.TickManager.Paused) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pause before fulfilling."); return false; }
            quest = NativeQuestOperations.Find(command.Quest.EntityId);
            if (quest == null || quest.State != QuestState.Ongoing)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact visible ongoing quest is unavailable."); return false; }
            if (NativeQuestOperations.Token(quest) != command.Quest.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Quest state changed; observe before new fulfillment."); return false; }
            caravan = Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled && c.GetUniqueLoadID() == command.Caravan.EntityId);
            if (caravan == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact player caravan is unavailable."); return false; }
            var pawnIds = caravan.PawnsListForReading.Select(p => p.GetUniqueLoadID()).ToArray();
            if (!pawnIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(command.ExpectedPawnIds.OrderBy(id => id, StringComparer.Ordinal)))
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Caravan membership changed; observe before new fulfillment."); return false; }
            if (CaravanToken(caravan.GetUniqueLoadID(), caravan.Tile.tileId, caravan.pather.Moving, pawnIds) != command.Caravan.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Caravan position changed; observe before new fulfillment."); return false; }
            var parts = quest.PartsListForReading.OfType<QuestPart_InitiateTradeRequest>().ToArray();
            if (parts.Length != 1) { failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "This quest does not have one native settlement trade objective."); return false; }
            var part = parts[0];
            var settlement = CaravanVisitUtility.SettlementVisitedNow(caravan);
            if (settlement == null || settlement != part.settlement || settlement.Faction == null || settlement.Faction.HostileTo(Faction.OfPlayer))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Visit the exact nonhostile quest settlement first."); return false; }
            request = settlement.GetComponent<TradeRequestComp>();
            if (request == null || !request.ActiveRequest || request.requestThingDef != part.requestedThingDef || request.requestCount != part.requestedCount)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Native trade objective changed or expired; observe before new fulfillment."); return false; }
            command2 = request.GetCaravanGizmos(caravan).OfType<Command_Action>().SingleOrDefault();
            if (command2 == null || command2.Disabled)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native fulfillment is disabled; inspect requested cargo quality, freshness and quantity."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.FulfillQuest command, Quest quest, bool fulfilled, string after) => new Receipts.EffectEvidence
        {
            Quest = new Receipts.QuestEffect
            {
                QuestId = command.Quest.EntityId, Accepted = fulfilled, State = quest.State.ToString(), AcceptanceTick = quest.acceptanceTick,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Quest.EntityId, BeforeToken = command.Quest.ExpectedSnapshotToken, AfterToken = after },
            }
        };

        internal static Operations.PreviewReply Preview(Operations.FulfillQuest? command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command!, out var quest, out _, out _, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = Evidence(command!, quest!, false, NativeQuestOperations.Token(quest!)) } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Quest fulfillment preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.FulfillQuest; var pre = request.Precondition;
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, out var quest, out var caravan, out var tradeRequest, out var gizmo, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success) throw new InvalidOperationException("Quest fulfillment authority changed before native effect.");
                    if (!Prepare(command, out quest, out caravan, out tradeRequest, out gizmo, out failure) || quest == null || caravan == null || tradeRequest == null || gizmo == null)
                        throw new InvalidOperationException("Quest fulfillment prerequisites changed after admission.");
                    var windows = Find.WindowStack.Windows.ToArray();
                    gizmo.action();
                    var confirmation = Find.WindowStack.Windows.OfType<Dialog_MessageBox>().SingleOrDefault(w => !windows.Contains(w));
                    bool fulfilled;
                    try
                    {
                        if (confirmation?.buttonAAction == null)
                            throw new InvalidOperationException("The native fulfillment confirmation was not observed; inspect before retrying.");
                        confirmation.buttonAAction();
                        confirmation.Close();
                    }
                    finally
                    {
                        fulfilled = !tradeRequest.ActiveRequest;
                        var after = NativeQuestOperations.Token(quest);
                        evidence = Evidence(command, quest, fulfilled, after);
                        // Recorded before the success check below throws, so a
                        // partial/uncertain outcome (the confirmation dialog was
                        // not observed, or the request did not clear) remains
                        // observable afterward -- the same "record before the
                        // success check" order NativeQuestOperations and
                        // NativeSettlementGiftOperations use.
                        state.QuestFulfills.Add(pre.Attempt.Clone(), new NativeQuestFulfillRecord(quest.GetUniqueLoadID()));
                    }
                    if (!fulfilled) throw new InvalidOperationException("Native fulfillment did not clear the trade request; inspect before retrying.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence!) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Quest fulfillment validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted quest fulfillment requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeQuestFulfillRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                // Mirrors NativeQuestOperations.Observe: does not filter on
                // hidden/hiddenInUI, since a fulfilled quest's hidden flag can
                // change afterward depending on its own definition, and that
                // must never read as "absent, so unsuccessful".
                var quest = global::Verse.Find.QuestManager.QuestsListForReading.SingleOrDefault(q => q.GetUniqueLoadID() == record.QuestId);
                if (quest == null)
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact quest is no longer observable; absence does not prove fulfillment held." };
                    return result;
                }
                result.CompleteInspection = true;
                var after = NativeQuestOperations.Token(quest);
                var parts = quest.PartsListForReading.OfType<QuestPart_InitiateTradeRequest>().ToArray();
                var settlement = parts.Length == 1 ? parts[0].settlement : null;
                var stillActive = settlement?.GetComponent<TradeRequestComp>()?.ActiveRequest ?? false;
                var evidence = new Receipts.EffectEvidence { Quest = new Receipts.QuestEffect {
                    QuestId = record.QuestId, Accepted = !stillActive, State = quest.State.ToString(),
                    AcceptanceTick = quest.acceptanceTick, Snapshot = new Receipts.SnapshotEvidence { EntityId = record.QuestId, AfterToken = after } } };
                if (!stillActive)
                    result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                        Detail = "The native trade request is still active; do not restore over player changes." };
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Quest fulfillment inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
