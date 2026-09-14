using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds one minimal not-yet-accepted
    // quest (no QuestGen node graph) carrying a two-option QuestPart_Choice
    // reward with no rewards attached to either option, so
    // NativeQuestOperations.Prepare's reward-choice branch is exercised
    // honestly without needing any concrete reward content. The quest
    // deliberately carries no QuestPart_RequirementsToAccept part, so
    // Quest.RequiresAccepter is false -- the same "requires an accepter"
    // vanilla mechanism (QuestPart_RequirementsToAcceptColonistWithTitle)
    // needs a held Royalty title, out of scope for a minimal fixture -- and
    // questacceptaccept instead exercises the "this quest does not accept an
    // accepter" refusal branch by supplying one anyway.
    public sealed class QuestAcceptFixture
    {
        [Tool("test/quest_accept_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one minimal not-yet-accepted quest with a two-option, reward-free QuestPart_Choice and no accepter requirement.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var colonists = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count < 1) return Refuse("At least one existing colonist is required.");

                var quest = new Quest {
                    id = Find.UniqueIDsManager.GetNextQuestID(),
                    name = "Fixture quest offer", description = "Private disposable fixture quest.",
                    acceptanceTick = -1, acceptanceExpireTick = -1,
                };
                var choice = quest.AddPart<QuestPart_Choice>();
                choice.choices.Add(new QuestPart_Choice.Choice());
                choice.choices.Add(new QuestPart_Choice.Choice());
                Find.QuestManager.Add(quest);

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    questId = quest.GetUniqueLoadID(),
                    pawnIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
