using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class QuestTools
    {
        [Tool("home/accept_quest", Title = "Accept an observed native quest",
            Description = "Preview or accept an exact visible quest through native eligibility and Quest.Accept. Does not award success, rewards or signals. Quest completion must be observed separately.")]
        public async Task<object> Accept(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact observed quest load ID")] string questId,
            [ToolParameter(Description = "Exact eligible colonist load ID")] string pawnId,
            [ToolParameter(Description = "Observed colony ID")] string colonyId,
            [ToolParameter(Description = "Observed load token")] string loadToken,
            [ToolParameter(Description = "Observed map ID")] int mapId,
            [ToolParameter(Description = "Exact observed reward choice index; -1 only for quests without choices", DefaultValue = -1)] int rewardChoice = -1,
            [ToolParameter(Description = "Preview only unless false", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (Current.Game == null || !Find.TickManager.Paused)
                    return Refuse("A loaded paused game is required");
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                if (identity == null || identity.ColonyId != colonyId || identity.LoadToken != loadToken ||
                    Find.CurrentMap?.uniqueID != mapId)
                    return Refuse("Colony, map or load changed; observe before quest acceptance");
                var quest = Find.QuestManager.QuestsListForReading.SingleOrDefault(q =>
                    q.GetUniqueLoadID() == questId && !q.hidden && !q.hiddenInUI);
                if (quest == null || quest.State != QuestState.NotYetAccepted)
                    return Refuse("Quest offer is unavailable or no longer awaiting acceptance");
                var choices = quest.PartsListForReading.OfType<QuestPart_Choice>().ToArray();
                if (choices.Length > 1) return Refuse("Multiple native choice parts require the quest interface");
                var choice = choices.SingleOrDefault();
                if ((choice == null && rewardChoice != -1) ||
                    (choice != null && (rewardChoice < 0 || rewardChoice >= choice.choices.Count)))
                    return Refuse("Select an exact observed native reward choice");
                var eligible = QuestUtility.CanAcceptQuest(quest);
                if (!eligible.Accepted) return Refuse(eligible.Reason ?? "Native quest requirements are not met");
                var pawn = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                    .SingleOrDefault(p => p.GetUniqueLoadID() == pawnId);
                if (pawn == null || !QuestUtility.CanPawnAcceptQuest(pawn, quest))
                    return Refuse("Colonist does not meet native quest acceptance requirements");
                if (!dryRun)
                {
                    if (choice != null) choice.Choose(choice.choices[rewardChoice]);
                    quest.Accept(quest.RequiresAccepter ? pawn : null);
                }
                return (object)new { success = true, accepted = dryRun || quest.EverAccepted,
                    dryRun, questId, state = quest.State.ToString(), acceptanceTick = quest.acceptanceTick };
            }, cancellationToken);
        }

        private static object Refuse(string reason) => new { success = true, accepted = false, reason };
    }
}
