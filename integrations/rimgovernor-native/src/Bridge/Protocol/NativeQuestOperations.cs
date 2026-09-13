#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for the quest-acceptance direct-write order: AcceptQuest.
    // Ports the legacy JSON home/accept_quest tool's (QuestTools.Accept)
    // eligibility checks behind the typed boundary. Like prisoner interaction
    // and husbandry, this is an immediate settings write with no native job:
    // Quest.Accept (and any single choice's Choose) either takes effect now
    // or it does not. FulfillQuest (settlement trade-request fulfillment) is
    // a separate, larger native mechanism -- it drives an actual caravan
    // gizmo callback and a native confirmation dialog -- and is not
    // implemented here.
    internal sealed class NativeQuestRecord
    {
        internal readonly string QuestId;
        internal NativeQuestRecord(string questId) { QuestId = questId; }
    }

    internal static class NativeQuestOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Mirrors NativePrisonerInteractionOperations.Settings: exact quest
        // acceptance-relevant state, so a change to any of it (including one
        // this same order just made) invalidates a stale expected_snapshot_token.
        internal static string Token(Quest quest) => "quest-" + Hash(string.Join("|",
            quest.GetUniqueLoadID(), quest.State.ToString(), quest.acceptanceTick.ToString(),
            QuestUtility.CanAcceptQuest(quest).Accepted.ToString()));

        internal static Quest? Find(string questId) => global::Verse.Find.QuestManager.QuestsListForReading
            .SingleOrDefault(q => q.GetUniqueLoadID() == questId && !q.hidden && !q.hiddenInUI);

        private static bool ValidCommand(Operations.AcceptQuest? command) => command != null && NativeDraftProtocol.ValidEntity(command.Quest);

        // Prepare re-validates everything native readback needs to agree on
        // before and after admission: exact quest identity/CAS token, at most
        // one native reward-choice part with an exact matching selection, and
        // (when required) an exact eligible accepter colonist.
        private static bool Prepare(Operations.AcceptQuest command, out Quest? quest, out Pawn? pawn, out QuestPart_Choice? choice, out Common.Failure failure)
        {
            quest = null; pawn = null; choice = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Quest acceptance requires an exact current quest settings snapshot.");
            if (!ValidCommand(command)) return false;
            quest = Find(command.Quest.EntityId);
            if (quest == null || quest.State != QuestState.NotYetAccepted)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact visible not-yet-accepted quest is unavailable."); return false; }
            if (Token(quest) != command.Quest.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Quest state changed; observe before new acceptance."); return false; }
            var choices = quest.PartsListForReading.OfType<QuestPart_Choice>().ToArray();
            if (choices.Length > 1)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Multiple native choice parts require the quest interface."); return false; }
            choice = choices.SingleOrDefault();
            var rewardChoice = command.HasRewardChoice ? command.RewardChoice : -1;
            if ((choice == null && rewardChoice != -1) || (choice != null && (rewardChoice < 0 || rewardChoice >= choice.choices.Count)))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Select an exact observed native reward choice."); return false; }
            var eligible = QuestUtility.CanAcceptQuest(quest);
            if (!eligible.Accepted)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, eligible.Reason ?? "Native quest requirements are not met."); return false; }
            if (quest.RequiresAccepter)
            {
                if (!command.HasAccepterPawnId)
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "This quest requires an exact accepter colonist."); return false; }
                pawn = global::Verse.Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                    .SingleOrDefault(p => p.GetUniqueLoadID() == command.AccepterPawnId);
                if (pawn == null || !QuestUtility.CanPawnAcceptQuest(pawn, quest))
                { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Colonist does not meet native quest acceptance requirements."); return false; }
            }
            else if (command.HasAccepterPawnId)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "This quest does not accept a native accepter colonist."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Operations.AcceptQuest command, Quest quest, string after) => new Receipts.EffectEvidence
        {
            Quest = new Receipts.QuestEffect
            {
                QuestId = command.Quest.EntityId, Accepted = quest.State != QuestState.NotYetAccepted, State = quest.State.ToString(),
                AcceptanceTick = quest.acceptanceTick,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Quest.EntityId, BeforeToken = command.Quest.ExpectedSnapshotToken, AfterToken = after },
            }
        };

        internal static Operations.PreviewReply Preview(Operations.AcceptQuest? command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command!, out var quest, out _, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = Evidence(command!, quest!, Token(quest!)) } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Quest acceptance preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.AcceptQuest; var pre = request.Precondition;
            NativeAttemptLedger.Admission? handle = null; Authority.Owner? owner = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, out var quest, out var pawn, out var choice, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                owner = new Authority.Owner { ControllerSessionId = guard.Snapshot.Lease!.ControllerSessionId, PlayerDirection = guard.Snapshot.Lease.PlayerDirection };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context, owner);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                    if (!current.Success) throw new InvalidOperationException("Quest acceptance authority changed before native effect.");
                    if (!Prepare(command, out quest, out pawn, out choice, out failure) || quest == null)
                        throw new InvalidOperationException("Quest acceptance prerequisites changed after admission.");
                    var rewardChoice = command.HasRewardChoice ? command.RewardChoice : -1;
                    if (choice != null) choice.Choose(choice.choices[rewardChoice]);
                    quest.Accept(quest.RequiresAccepter ? pawn : null);
                    var after = Token(quest);
                    state.Quests.Add(pre.Attempt.Clone(), new NativeQuestRecord(quest.GetUniqueLoadID()));
                    evidence = Evidence(command, quest, after);
                    if (quest.State == QuestState.NotYetAccepted) throw new InvalidOperationException("Native quest acceptance readback did not apply.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, owner, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Quest acceptance validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, owner!, evidence!, "Admitted quest acceptance requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeQuestRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                // Quest.hidden can flip after acceptance depending on the quest's
                // own definition, so this lookup (unlike Find/Prepare) does not
                // filter on hidden/hiddenInUI: an accepted quest disappearing from
                // that filtered view must never read as "absent, so unsuccessful".
                var quest = global::Verse.Find.QuestManager.QuestsListForReading.SingleOrDefault(q => q.GetUniqueLoadID() == record.QuestId);
                if (quest == null)
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact quest is no longer observable; absence does not prove acceptance held." };
                    return result;
                }
                result.CompleteInspection = true;
                var after = Token(quest);
                var evidence = new Receipts.EffectEvidence { Quest = new Receipts.QuestEffect {
                    QuestId = record.QuestId, Accepted = quest.State != QuestState.NotYetAccepted, State = quest.State.ToString(),
                    AcceptanceTick = quest.acceptanceTick, Snapshot = new Receipts.SnapshotEvidence { EntityId = record.QuestId, AfterToken = after } } };
                if (quest.State != QuestState.NotYetAccepted)
                    result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                        Detail = "The quest is still not yet accepted; do not restore over player changes." };
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Quest acceptance inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
