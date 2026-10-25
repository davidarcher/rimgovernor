using System;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // The AcceptQuestIntent arm of Actions/Apply: accept one visible
    // quest offer under the game's own acceptance rules. An immediate settings
    // write with no native job: Quest.Accept (and a single choice part's
    // Choose) takes effect now or not at all. A quest already accepted is
    // applied again without a second Accept.
    internal static class NativeQuestOperations
    {
        internal static Quest? Find(string questId) => global::Verse.Find.QuestManager.QuestsListForReading
            .ById(questId) is Quest q && !q.hidden && !q.hiddenInUI ? q : null;

        // Resolve checks what the game checks before Quest.Accept: a visible
        // not-yet-accepted quest, at most one choice part with the selected
        // option in range, CanAcceptQuest, and the accepter when required.
        // accepted is true when the quest is already accepted; Quest.hidden
        // can flip after acceptance, so that lookup is unfiltered.
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.AcceptQuestIntent? command, out Quest? quest, out Pawn? pawn, out QuestPart_Choice? choice, out bool accepted)
        {
            quest = null; pawn = null; choice = null; accepted = false;
            if (command == null || command.QuestId is null || !GameThread.IsIdentifier(command.QuestId) || (command.AccepterPawnId is not null && !GameThread.IsIdentifier(command.AccepterPawnId)))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A quest acceptance requires a quest id.");
            var id = (command.QuestId ?? "");
            var any = global::Verse.Find.QuestManager.QuestsListForReading.ById(id);
            if (any != null && any.State != QuestState.NotYetAccepted && any.acceptanceTick >= 0)
            { quest = any; accepted = true; return null; }
            quest = Find(id);
            if (quest == null || quest.State != QuestState.NotYetAccepted)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact visible not-yet-accepted quest is unavailable.");
            var choices = quest.PartsListForReading.OfType<QuestPart_Choice>().ToArray();
            if (choices.Length > 1)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unsupported, "Multiple native choice parts require the quest interface.");
            choice = choices.SingleOrDefault();
            if (choice == null ? command.RewardChoice is not RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection.NoChoice
                : command.RewardChoice is not RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection.Choose { Index: >= 0 } selected || selected.Index >= choice.choices.Count)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Select an exact observed native reward choice.");
            var eligible = QuestUtility.CanAcceptQuest(quest);
            if (!eligible.Accepted)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, eligible.Reason ?? "Native quest requirements are not met.");
            if (quest.RequiresAccepter)
            {
                if (command.AccepterPawnId is null)
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "This quest requires an exact accepter colonist.");
                var accepter = (command.AccepterPawnId ?? "");
                pawn = global::Verse.Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned).ById(accepter);
                if (pawn == null || !QuestUtility.CanPawnAcceptQuest(pawn, quest))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Colonist does not meet native quest acceptance requirements.");
            }
            else if (command.AccepterPawnId is not null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "This quest does not accept a native accepter colonist.");
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence(Quest quest) => new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence
        {
            Effect = new RimGovernor.Brain.GameFacts.Receipts.QuestEffect
            {
                QuestId = quest.GetUniqueLoadID(), Accepted = quest.State != QuestState.NotYetAccepted,
            }
        };

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.AcceptQuestIntent command) => Resolve(command, out _, out _, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.AcceptQuestIntent command)
        {
            var failure = Resolve(command, out var quest, out var pawn, out var choice, out var accepted);
            if (failure != null || quest == null) throw new InvalidOperationException("Quest acceptance prerequisites changed before apply: " + (failure?.Detail ?? ""));
            if (accepted) return Evidence(quest);
            if (choice != null) choice.Choose(choice.choices[((RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection.Choose)command.RewardChoice).Index]);
            quest.Accept(quest.RequiresAccepter ? pawn : null);
            if (quest.State == QuestState.NotYetAccepted) throw new InvalidOperationException("Native quest acceptance readback did not apply.");
            return Evidence(quest);
        }
    }

    internal sealed class AcceptQuestActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeQuestOperations.Validate(action.Intent?.AcceptQuest ?? throw new InvalidOperationException("Action carries no accept-quest intent."));
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeQuestOperations.Apply(action.Intent?.AcceptQuest ?? throw new InvalidOperationException("Action carries no accept-quest intent."));
    }
}
