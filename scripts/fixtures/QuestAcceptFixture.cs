using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.QuestGen;
using Verse;
using Verse.AI;

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
    //
    // test/joiner_quest_prepare (#250) instead generates a real
    // ThreatReward_Raid_Joiner offer through the native storyteller path
    // (QuestUtility.GenerateQuestAndMakeAvailable at the map's default
    // threat points), plus the spare unowned sleeping spots and food the
    // colony needs before MaintainPopulation may answer it; the quest's own
    // parts then walk the joiner in and bring the raid after them.
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

        [Tool("test/joiner_quest_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: generate one real not-yet-accepted ThreatReward_Raid_Joiner offer through the native storyteller path and spawn spare unowned sleeping spots and survival meals beside the colonists.")]
        public async Task<object> PrepareJoiner(IRimBridgeContext ctx, CancellationToken cancellationToken,
            int spareBeds = 2, int mealPacks = 40, float minPoints = 300f)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (spareBeds < 0 || spareBeds > 8 || mealPacks < 0 || mealPacks > 200) return Refuse("Fixture counts out of range.");
                var colonists = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count < 1) return Refuse("At least one existing colonist is required.");

                var def = DefDatabase<QuestScriptDef>.GetNamed("ThreatReward_Raid_Joiner");
                // The quiet baseline sits at the storyteller's 35-point floor, below what
                // Util_Raid can generate a raider for; the offer is generated at the larger
                // of the map's own points and minPoints (the raid only follows the joiner).
                var points = Math.Max(StorytellerUtility.DefaultThreatPointsNow(map), minPoints);
                var slate = new Slate();
                slate.Set("points", points);
                // The quiet storyteller disallows violent quests, which QuestNode_Raid's
                // TestRun honours; the flag only gates selection and generation, so it is
                // lifted for this one generation and restored (threat scale stays zero
                // and the quiet marker is unchanged).
                var difficulty = Find.Storyteller.difficulty;
                var allowViolent = difficulty.allowViolentQuests;
                difficulty.allowViolentQuests = true;
                Quest quest;
                try
                {
                    if (!def.root.TestRun(slate)) return Refuse("ThreatReward_Raid_Joiner root TestRun refused at " + points + " points.");
                    quest = QuestUtility.GenerateQuestAndMakeAvailable(def, points);
                }
                finally { difficulty.allowViolentQuests = allowViolent; }
                if (quest == null || quest.State != QuestState.NotYetAccepted || quest.hidden)
                    return Refuse("The generated joiner quest is not a visible not-yet-accepted offer.");

                // Spare beds and food beside the first colonist: a bare
                // sleeping spot is a humanlike, non-medical, non-prisoner bed
                // with no owner, exactly the spare bed policy.JoinerCapacity
                // looks for; the meals lift the stock food runway past any
                // policy reserve the case declares.
                var anchor = colonists[0].Position;
                var used = new HashSet<IntVec3>();
                Func<IntVec3> place = () => {
                    IntVec3 cell;
                    if (!CellFinder.TryFindRandomCellNear(anchor, map, 14, c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map)
                            && c.GetEdifice(map) == null && c.GetFirstItem(map) == null && !used.Contains(c)
                            && colonists[0].CanReach(c, PathEndMode.OnCell, Danger.Deadly), out cell))
                        throw new InvalidOperationException("No free standable cell near the colonists");
                    used.Add(cell);
                    return cell;
                };
                var beds = new List<string>();
                for (int i = 0; i < spareBeds; i++)
                {
                    var spot = ThingMaker.MakeThing(ThingDef.Named("SleepingSpot"));
                    spot.SetFaction(player);
                    var cell = place();
                    GenSpawn.Spawn(spot, cell, map);
                    spot.SetForbidden(false, false);
                    map.areaManager.Home[cell] = true;
                    beds.Add(spot.GetUniqueLoadID());
                }
                var meals = 0;
                for (int i = 0; i < mealPacks; i++)
                {
                    var food = ThingMaker.MakeThing(ThingDef.Named("MealSurvivalPack"));
                    food.stackCount = food.def.stackLimit;
                    GenSpawn.Spawn(food, place(), map);
                    food.SetForbidden(false, false);
                    meals += food.stackCount;
                }

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, points,
                    questId = quest.GetUniqueLoadID(), scriptDef = quest.root?.defName, state = quest.State.ToString(),
                    expireTick = quest.acceptanceExpireTick, requiresAccepter = quest.RequiresAccepter,
                    beds = beds.ToArray(), meals,
                    colonists = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/joiner_quest_read", Description = "Private disposable fixture read: one quest's native state and acceptance tick, the free colonists and the map's spawned player-faction beds with their owners.")]
        public async Task<object> ReadJoiner(IRimBridgeContext ctx, CancellationToken cancellationToken, string questId = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null) return Refuse("A loaded colony map is required.");
                var quest = Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.GetUniqueLoadID() == questId);
                var beds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>()
                    .Select(b => new { id = b.GetUniqueLoadID(), medical = b.Medical, prisoners = b.ForPrisoners,
                        owners = b.OwnersForReading.Select(p => p.GetUniqueLoadID()).ToArray() }).ToArray();
                return new {
                    success = true, tick = Find.TickManager.TicksGame,
                    questFound = quest != null, state = quest?.State.ToString(), acceptedTick = quest?.acceptanceTick,
                    hidden = quest?.hidden, scriptDef = quest?.root?.defName,
                    colonists = map.mapPawns.FreeColonistsSpawned.Select(p => p.GetUniqueLoadID()).OrderBy(id => id).ToArray(),
                    beds,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
