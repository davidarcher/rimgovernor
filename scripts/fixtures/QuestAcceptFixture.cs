using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.QuestGen;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Builds one minimal not-yet-accepted
    // quest (no QuestGen node graph) carrying a two-option QuestPart_Choice
    // reward with no rewards attached to either option, so
    // NativeQuestOperations.Resolve's reward-choice branch is exercised
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
            int spareBeds = 2, int mealPacks = 40, float minPoints = 300f, bool skipQuest = false)
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
                Quest quest = null;
                try
                {
                    if (!skipQuest)
                    {
                        if (!def.root.TestRun(slate)) return Refuse("ThreatReward_Raid_Joiner root TestRun refused at " + points + " points.");
                        quest = QuestUtility.GenerateQuestAndMakeAvailable(def, points);
                    }
                }
                finally { difficulty.allowViolentQuests = allowViolent; }
                if (!skipQuest && (quest == null || quest.State != QuestState.NotYetAccepted || quest.hidden))
                    return Refuse("The generated joiner quest is not a visible not-yet-accepted offer.");
                // The offer's own window (~0.3 days) ran out while the supervised
                // windows played the startup days before the planner accepted it
                // (#717); the case proves the answer, not the race, so hold the
                // offer open for thirty days.
                if (quest != null) quest.acceptanceExpireTick = Find.TickManager.TicksGame + 30 * GenDate.TicksPerDay;

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
                    questId = quest?.GetUniqueLoadID(), scriptDef = quest?.root?.defName, state = quest?.State.ToString(),
                    expireTick = quest?.acceptanceExpireTick, requiresAccepter = quest?.RequiresAccepter,
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

        // test/royal_title_prepare (#1613): the first-title staging. The lab holds
        // no Empire quest and no bedroom, so this generates the Empire when the
        // map has none, keeps it neutral, furnishes one walled, roofed bedroom
        // that meets the first title's bedroom requirements with the first
        // colonist owning its Bed, and offers one not-yet-accepted Empire quest
        // whose single reward choice is exactly the favor that title costs (a
        // QuestPart_GiveRoyalFavor on the quest's own accept signal gives it, the
        // choice's reward row is what the quest census reads). Everything after
        // the offer (accepting it, the natively generated bestowing ceremony
        // quest, the ceremony and the title) is the game's and the controller's.
        [Tool("test/royal_title_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#1613): furnish a first-title bedroom for the first colonist and offer one not-yet-accepted Empire quest granting the first title's favor. Requires Royalty.")]
        public async Task<object> PrepareRoyalTitle(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.RoyaltyActive) return Refuse("Royalty is not active.");
                var colonist = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed)
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (colonist == null) return Refuse("At least one existing colonist is required.");

                var empire = Find.FactionManager.OfEmpire;
                var generated = false;
                if (empire == null)
                {
                    empire = FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(FactionDefOf.Empire));
                    Find.FactionManager.Add(empire);
                    generated = true;
                }
                if (empire.HostileTo(player)) empire.SetRelationDirect(player, FactionRelationKind.Neutral, false);
                if (colonist.royalty == null) colonist.royalty = new Pawn_RoyaltyTracker(colonist);
                var title = empire.def.RoyalTitlesAwardableInSeniorityOrderForReading.FirstOrDefault();
                if (title == null) return Refuse("The Empire awards no royal title.");
                if (colonist.royalty.GetCurrentTitle(empire) != null) return Refuse("The colonist already holds an Empire title.");
                var needsThrone = title.throneRoomRequirements != null && title.throneRoomRequirements.Count > 0;

                // The bedroom: a 7x7 roofed room north-west of the centre, a Bed the
                // colonist owns, then whatever the title's bedroom requirements still
                // ask for, then a little decoration for the impressiveness.
                var stone = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
                var c0 = map.Center;
                var inside = new CellRect(c0.x - 9, c0.z + 4, 7, 7);
                var shell = inside.ExpandedBy(1);
                var door = new IntVec3(inside.CenterCell.x, 0, shell.minZ);
                foreach (var c in shell.Cells)
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    map.areaManager.Home[c] = true;
                    if (inside.Contains(c)) continue;
                    Place(map, c == door ? ThingDefOf.Door : ThingDefOf.Wall, stone, c);
                }
                var bed = (Building_Bed)Place(map, ThingDefOf.Bed, stone, new IntVec3(inside.minX, 0, inside.maxZ - 1));
                colonist.ownership.ClaimBedIfNonMedical(bed);
                var free = inside.Cells.Where(c => c.z < inside.maxZ - 1 && c.z > inside.minZ).ToList();
                var next = 0;
                Func<ThingDef, bool> furnish = def => {
                    while (next < free.Count)
                    {
                        var cell = free[next++];
                        if (!GenConstruct.CanPlaceBlueprintAt(def, cell, Rot4.North, map).Accepted) continue;
                        Place(map, def, stone, cell);
                        return true;
                    }
                    return false;
                };
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var room = inside.CenterCell.GetRoom(map);
                if (room == null || !room.ProperRoom || room.TouchesMapEdge || room.OpenRoofCount > 0)
                    return Refuse("The fixture bedroom is not an enclosed roofed room.");
                foreach (var req in title.bedroomRequirements ?? new List<RoomRequirement>())
                {
                    var defs = new List<ThingDef>(); var count = 1;
                    switch (req)
                    {
                        case RoomRequirement_ThingAnyOfCount anyCount: defs = anyCount.things; count = anyCount.count; break;
                        case RoomRequirement_ThingAnyOf any: defs = any.things; break;
                        case RoomRequirement_ThingCount counted: defs = new List<ThingDef> { counted.thingDef }; count = counted.count; break;
                        case RoomRequirement_Thing one: defs = new List<ThingDef> { one.thingDef }; break;
                    }
                    var def = defs.FirstOrDefault(d => d != null && d != ThingDefOf.Bed);
                    if (def == null) continue;
                    for (var i = 0; i < count && !req.Met(room, colonist); i++)
                        if (!furnish(def)) return Refuse("No room left to furnish " + def.defName + ".");
                }
                foreach (var name in new[] { "Dresser", "EndTable", "StandingLamp", "PlantPot" })
                {
                    var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def != null) furnish(def);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                room = inside.CenterCell.GetRoom(map);
                var unmet = (title.bedroomRequirements ?? new List<RoomRequirement>())
                    .Where(r => !r.Met(room, colonist)).Select(r => r.GetType().Name).ToArray();
                if (unmet.Length > 0) return Refuse("The fixture bedroom still fails " + title.defName + ": " + string.Join("; ", unmet));

                var favor = title.favorCost;
                var quest = new Quest {
                    id = Find.UniqueIDsManager.GetNextQuestID(),
                    name = "Fixture Empire quest", description = "Private disposable fixture Empire quest.",
                    acceptanceTick = -1, acceptanceExpireTick = Find.TickManager.TicksGame + 30 * GenDate.TicksPerDay,
                };
                var give = quest.AddPart<QuestPart_GiveRoyalFavor>();
                give.inSignal = quest.InitiateSignal;
                give.giveTo = colonist; give.faction = empire; give.amount = favor;
                var choice = quest.AddPart<QuestPart_Choice>();
                var reward = new Reward_RoyalFavor { faction = empire, amount = favor };
                var option = new QuestPart_Choice.Choice();
                option.rewards.Add(reward);
                choice.choices.Add(option);
                Find.QuestManager.Add(quest);

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    questId = quest.GetUniqueLoadID(), pawnId = colonist.GetUniqueLoadID(), bedId = bed.GetUniqueLoadID(),
                    title = title.defName, favor, needsThrone, empireGenerated = generated,
                    impressiveness = room?.GetStat(RoomStatDefOf.Impressiveness) ?? -1f,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Thing Place(Map map, ThingDef def, ThingDef stuff, IntVec3 cell)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(thing, cell, map, Rot4.North);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
