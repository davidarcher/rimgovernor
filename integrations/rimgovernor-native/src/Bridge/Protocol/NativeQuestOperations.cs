#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The AcceptQuestIntent arm of Actions/Apply (#942): accept one visible
    // quest offer under the game's own acceptance rules. An immediate settings
    // write with no native job: Quest.Accept (and a single choice part's
    // Choose) takes effect now or not at all. A quest already accepted is
    // applied again without a second Accept.
    internal static class NativeQuestOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Exact quest acceptance-relevant state, the snapshot token the
        // world-progression census reports.
        internal static string Token(Quest quest) => "quest-" + Hash(string.Join("|",
            quest.GetUniqueLoadID(), quest.State.ToString(), quest.acceptanceTick.ToString(),
            QuestUtility.CanAcceptQuest(quest).Accepted.ToString()));

        internal static Quest? Find(string questId) => global::Verse.Find.QuestManager.QuestsListForReading
            .ById(questId) is Quest q && !q.hidden && !q.hiddenInUI ? q : null;

        // Resolve checks what the game checks before Quest.Accept: a visible
        // not-yet-accepted quest, at most one choice part with the selected
        // option in range, CanAcceptQuest, and the accepter when required.
        // accepted is true when the quest is already accepted; Quest.hidden
        // can flip after acceptance, so that lookup is unfiltered.
        private static Common.Failure? Resolve(Operations.AcceptQuestIntent? command, out Quest? quest, out Pawn? pawn, out QuestPart_Choice? choice, out bool accepted)
        {
            quest = null; pawn = null; choice = null; accepted = false;
            if (command == null || !command.HasQuestId || !ProtoBoundary.IsIdentifier(command.QuestId) || (command.HasAccepterPawnId && !ProtoBoundary.IsIdentifier(command.AccepterPawnId)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A quest acceptance requires a quest id.");
            var id = command.QuestId;
            var any = global::Verse.Find.QuestManager.QuestsListForReading.ById(id);
            if (any != null && any.State != QuestState.NotYetAccepted && any.acceptanceTick >= 0)
            { quest = any; accepted = true; return null; }
            quest = Find(id);
            if (quest == null || quest.State != QuestState.NotYetAccepted)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact visible not-yet-accepted quest is unavailable.");
            var choices = quest.PartsListForReading.OfType<QuestPart_Choice>().ToArray();
            if (choices.Length > 1)
                return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Multiple native choice parts require the quest interface.");
            choice = choices.SingleOrDefault();
            var rewardChoice = command.HasRewardChoice ? command.RewardChoice : -1;
            if ((choice == null && rewardChoice != -1) || (choice != null && (rewardChoice < 0 || rewardChoice >= choice.choices.Count)))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Select an exact observed native reward choice.");
            var eligible = QuestUtility.CanAcceptQuest(quest);
            if (!eligible.Accepted)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, eligible.Reason ?? "Native quest requirements are not met.");
            if (quest.RequiresAccepter)
            {
                if (!command.HasAccepterPawnId)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "This quest requires an exact accepter colonist.");
                var accepter = command.AccepterPawnId;
                pawn = global::Verse.Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).ById(accepter);
                if (pawn == null || !QuestUtility.CanPawnAcceptQuest(pawn, quest))
                    return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Colonist does not meet native quest acceptance requirements.");
            }
            else if (command.HasAccepterPawnId)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "This quest does not accept a native accepter colonist.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Quest quest) => new Receipts.EffectEvidence
        {
            Quest = new Receipts.QuestEffect
            {
                QuestId = quest.GetUniqueLoadID(), Accepted = quest.State != QuestState.NotYetAccepted, State = NativeEnums.Quest(quest.State),
                AcceptanceTick = quest.acceptanceTick,
                Snapshot = new Receipts.SnapshotEvidence { EntityId = quest.GetUniqueLoadID(), AfterToken = Token(quest) },
            }
        };

        internal static Common.Failure? Validate(Operations.AcceptQuestIntent command) => Resolve(command, out _, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.AcceptQuestIntent command)
        {
            var failure = Resolve(command, out var quest, out var pawn, out var choice, out var accepted);
            if (failure != null || quest == null) throw new InvalidOperationException("Quest acceptance prerequisites changed before apply: " + failure?.Detail);
            if (accepted) return Evidence(quest);
            if (choice != null) choice.Choose(choice.choices[command.RewardChoice]);
            quest.Accept(quest.RequiresAccepter ? pawn : null);
            if (quest.State == QuestState.NotYetAccepted) throw new InvalidOperationException("Native quest acceptance readback did not apply.");
            return Evidence(quest);
        }
    }

    internal sealed class AcceptQuestActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeQuestOperations.Validate(action.AcceptQuest);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeQuestOperations.Apply(action.AcceptQuest);
    }
}
