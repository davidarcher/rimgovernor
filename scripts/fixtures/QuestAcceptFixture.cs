using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.QuestGen;
using RimWorld.Planet;
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
    // test/joiner_quest_prepare instead generates a real
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
                //; the case proves the answer, not the race, so hold the
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

        // test/royal_title_prepare: the first-title staging. The lab holds
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

        [Tool("test/quest_hospitality_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable lab: real Hospitality_Joiners offer, titled lodger, vacant furnished bedroom and short hosting interval.")]
        public async Task<object> PrepareHospitality(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused || !ModsConfig.RoyaltyActive) return Refuse("Paused Royalty lab required.");
                var empire = FixtureEmpire();
                Quest quest = null;
                for(var attempt=0;attempt<16;attempt++) {
                    var slate = new Slate(); slate.Set("points", 300f); slate.Set("lodgersCount", 1);
                    var candidate=RimWorld.QuestGen.QuestGen.Generate(DefDatabase<QuestScriptDef>.GetNamed("Hospitality_Joiners"),slate);
                    var guests=candidate.PartsListForReading.OfType<QuestPart_RequirementsToAcceptBedroom>().SelectMany(p=>p.targetPawns).ToArray();
                    if(guests.Length==1 && !guests[0].Downed && !candidate.PartsListForReading.OfType<QuestPart_AddHediff>().Any(p=>p.pawns.Contains(guests[0]))) {quest=candidate;break;}
                    candidate.CleanupQuestParts();
                }
                if(quest==null) return Refuse("Could not generate a healthy one-lodger hospitality precondition.");
                Find.QuestManager.Add(quest);
                var requirements = quest.PartsListForReading.OfType<QuestPart_RequirementsToAcceptBedroom>().Single();
                var lodger = requirements.targetPawns.First();
                // Title is a precondition. The generated graph retains its real
                // bedroom requirement, arrival, pickup and end signals.
                var title = empire.def.RoyalTitlesAwardableInSeniorityOrderForReading.First(t => t.bedroomRequirements != null && t.bedroomRequirements.Count > 0);
                if (lodger.royalty == null) lodger.royalty = new Pawn_RoyaltyTracker(lodger);
                lodger.royalty.SetTitle(empire, title, false, false, false);
                foreach (var p in quest.PartsListForReading.OfType<QuestPart_ShuttleDelay>()) p.delayTicks = 8000;
                quest.acceptanceExpireTick = Find.TickManager.TicksGame + 30 * GenDate.TicksPerDay;
                var stone = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
                var inside = new CellRect(map.Center.x-15,map.Center.z+5,9,9);
                var shell = inside.ExpandedBy(1); var door = new IntVec3(inside.CenterCell.x,0,shell.minZ);
                foreach(var cell in shell.Cells) {
                    map.roofGrid.SetRoof(cell,RoofDefOf.RoofConstructed); map.areaManager.Home[cell]=true;
                    if(!inside.Contains(cell)) Place(map,cell==door?ThingDefOf.Door:ThingDefOf.Wall,stone,cell);
                    else map.terrainGrid.SetTerrain(cell,DefDatabase<TerrainDef>.GetNamed("TileMarble"));
                }
                var bed = (Building_Bed)Place(map,ThingDefOf.Bed,stone,new IntVec3(inside.minX+1,0,inside.maxZ-2));
                var cells = inside.Cells.Where(c=>c.z<inside.maxZ-2 && c.z>inside.minZ).ToArray(); var index=0;
                Action<ThingDef> furnish=def=> {
                    while(index<cells.Length) { var c=cells[index++]; if(!GenConstruct.CanPlaceBlueprintAt(def,c,Rot4.North,map).Accepted) continue; Place(map,def,stone,c); return; }
                    throw new InvalidOperationException("Bedroom fixture has no furniture space.");
                };
                foreach(var name in new[]{"Dresser","EndTable","SculptureLarge"}) furnish(DefDatabase<ThingDef>.GetNamed(name));
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                var room = bed.GetRoom();
                foreach(var req in title.bedroomRequirements) {
                    var defs=new List<ThingDef>(); var count=1;
                    if(req is RoomRequirement_ThingAnyOfCount many) { defs=many.things; count=many.count; }
                    else if(req is RoomRequirement_ThingAnyOf any) defs=any.things;
                    else if(req is RoomRequirement_ThingCount counted) { defs.Add(counted.thingDef); count=counted.count; }
                    else if(req is RoomRequirement_Thing one) defs.Add(one.thingDef);
                    var def=defs.FirstOrDefault(d=>d!=ThingDefOf.Bed);
                    if(def!=null) for(var i=0;i<count && !req.Met(room,lodger);i++) furnish(def);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                if(!requirements.CanAccept().Accepted) return Refuse("Generated titled lodger bedroom requirement unmet: "+requirements.CanAccept().Reason);
                FixtureMeals(map);
                var witness=quest.AddPart<QuestHospitalityWitness>(); witness.inSignalEnable=quest.InitiateSignal; witness.pawn=lodger; witness.bed=bed;
                return new { success=true, questId=quest.GetUniqueLoadID(), pawnId=lodger.GetUniqueLoadID(),bedId=bed.GetUniqueLoadID(),title=title.defName,scriptDef=quest.root.defName };
            },cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_decree_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable lab: real produce and harvest decree signal graphs, raw cloth, hand tailoring bench and mature rice; no bills or zones supplied.")]
        public async Task<object> PrepareDecree(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map=Find.CurrentMap;
                if(map==null || !Find.TickManager.Paused || !ModsConfig.RoyaltyActive) return Refuse("Paused Royalty lab required.");
                var pawn=map.mapPawns.FreeColonistsSpawned.OrderBy(p=>p.thingIDNumber).First(); var empire=FixtureEmpire();
                // Only the staged plot is fertile, so the ordinary field site
                // chooser cannot plant somewhere that needs days of growth.
                foreach(var cell in map.AllCells) map.terrainGrid.SetTerrain(cell,DefDatabase<TerrainDef>.GetNamed("Concrete"));
                if(pawn.royalty==null) pawn.royalty=new Pawn_RoyaltyTracker(pawn);
                pawn.royalty.SetTitle(empire,empire.def.RoyalTitlesAwardableInSeniorityOrderForReading.First(t=>t.decreeTags!=null && t.decreeTags.Count>0),false,false,false);
                foreach(var worker in map.mapPawns.FreeColonistsSpawned) {
                    worker.skills.GetSkill(SkillDefOf.Crafting).Level=12; worker.skills.GetSkill(SkillDefOf.Plants).Level=12;
                    worker.workSettings.EnableAndInitialize();
                    worker.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Tailoring"),1); worker.workSettings.SetPriority(WorkTypeDefOf.Growing,1);
                    worker.workSettings.SetPriority(WorkTypeDefOf.PlantCutting,1);
                }
                var bench=(Building_WorkTable)Place(map,DefDatabase<ThingDef>.GetNamed("HandTailoringBench"),ThingDefOf.WoodLog,map.Center+new IntVec3(-8,0,-7));
                var cloth=ThingMaker.MakeThing(ThingDefOf.Cloth); cloth.stackCount=200; GenSpawn.Spawn(cloth,map.Center+new IntVec3(-7,0,-4),map);
                var crop=DefDatabase<ThingDef>.GetNamed("Plant_Rice");
                var plot=new CellRect(map.Center.x+5,map.Center.z-10,3,3);
                foreach(var c in plot.Cells) { map.terrainGrid.SetTerrain(c,TerrainDefOf.SoilRich); map.areaManager.Home[c]=true; var plant=(Plant)GenSpawn.Spawn(ThingMaker.MakeThing(crop),c,map); plant.Growth=1f; }
                FixtureMeals(map);
                Func<string,Quest> generate=name=> { var slate=new Slate();slate.Set("points",100f);slate.Set("asker",pawn); return QuestUtility.GenerateQuestAndMakeAvailable(DefDatabase<QuestScriptDef>.GetNamed(name),slate); };
                var produce=generate("Decree_ProduceItem");
                var requirement=produce.PartsListForReading.OfType<QuestPart_ThingsProduced>().Single();
                requirement.def=DefDatabase<ThingDef>.GetNamed("Apparel_TribalA"); requirement.stuff=ThingDefOf.Cloth; requirement.count=1;
                var harvest=generate("Decree_HarvestCrop");
                var harvesting=harvest.PartsListForReading.OfType<QuestPart_PlantsHarvested>().Single();
                harvesting.plant=crop.plant.harvestedThingDef; harvesting.count=1;
                return new { success=true,produceQuest=produce.GetUniqueLoadID(),harvestQuest=harvest.GetUniqueLoadID(),benchId=bench.GetUniqueLoadID(),product=requirement.def.defName,crop=crop.defName,plot=new[]{plot.minX,plot.minZ,plot.Width,plot.Height} };
            },cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_lifecycle_read", Description = "UNSAFE FOR MODEL EXECUTION. Read actual fixture guest hosting/boarding, bills, growing zones and products; never supplies completion.")]
        public async Task<object> ReadLifecycle(IRimBridgeContext ctx,CancellationToken cancellationToken,string questId)
        {
            return await ctx.MainThread.InvokeAsync<object>(()=> {
                var quest=Find.QuestManager.QuestsListForReading.FirstOrDefault(q=>q.GetUniqueLoadID()==questId);
                var witness=quest?.PartsListForReading.OfType<QuestHospitalityWitness>().FirstOrDefault();
                var map=Find.CurrentMap;
                return new { success=quest!=null,hosted=witness?.hosted??false,bedAssigned=witness?.assigned??false,boarded=witness?.boarded??false,
                    bills=map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>().Sum(b=>b.BillStack.Bills.Count),
                    growingZones=map.zoneManager.AllZones.OfType<Zone_Growing>().Count(),
                    products=map.listerThings.AllThings.Concat(map.mapPawns.AllPawnsSpawned.SelectMany(p=>p.apparel?.WornApparel.Cast<Thing>()??Enumerable.Empty<Thing>())).Distinct().Count(t=>t.def.defName=="Apparel_TribalA" && t.Stuff==ThingDefOf.Cloth) };
            },cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_expedition_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage a real neighboring bandit quest, equipped crew and home food; never forms a caravan, boards a shuttle or completes combat.")]
        public async Task<object> PrepareExpedition(IRimBridgeContext ctx, CancellationToken cancellationToken, bool shuttleMission=false)
        {
            return await ctx.MainThread.InvokeAsync<object>(()=> {
                var home=Find.CurrentMap;
                if(home==null || !Find.TickManager.Paused) return Refuse("Paused lab required.");
                var crew=home.mapPawns.FreeColonistsSpawned.OrderBy(p=>p.thingIDNumber).ToList();
                if(crew.Count<6) return Refuse("Six lab colonists required.");
                if(shuttleMission) FixtureEmpire();
                var neighbours=new List<PlanetTile>();Find.WorldGrid.GetTileNeighbors(home.Tile,neighbours);
                var tile=neighbours.FirstOrDefault(t=>!Find.World.Impassable(t) && !Find.WorldObjects.AnyMapParentAt(t));
                if(!tile.Valid) return Refuse("No adjacent passable site tile.");
                foreach(var pawn in crew) {
                    pawn.skills.GetSkill(SkillDefOf.Shooting).Level=18;
                    pawn.skills.GetSkill(SkillDefOf.Melee).Level=18;
                    if(pawn.equipment.Primary!=null) pawn.equipment.DestroyEquipment(pawn.equipment.Primary);
                    pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named("Gun_AssaultRifle")));
                    foreach(var item in new[]{"Apparel_FlakVest","Apparel_FlakHelmet"}) pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(ThingDef.Named(item)));
                    pawn.workSettings.EnableAndInitialize();
                    foreach(var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        if(!pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work,3);
                }
                FixtureMeals(home);
                for(var i=0;i<crew.Count;i++) Place(home,ThingDefOf.Bed,ThingDefOf.WoodLog,home.Center+new IntVec3(-8+i*2,0,-8));
                var script=shuttleMission?"Mission_BanditCamp":"OpportunitySite_BanditCamp";
                var slate=new Slate();slate.Set("map",home);slate.Set("points",shuttleMission?4000f:350f);
                var quest=RimWorld.QuestGen.QuestGen.Generate(DefDatabase<QuestScriptDef>.GetNamed(script),slate);
                var site=quest.QuestLookTargets.Where(t=>t.HasWorldObject).Select(t=>t.WorldObject).OfType<Site>().Distinct().SingleOrDefault();
                if(site==null) return Refuse("Generated quest lacks one exact bandit site.");
                site.Tile=tile;
                // The generated bandit part still creates genuine armed enemies;
                // only its fixture budget is reduced to a small fast fight.
                foreach(var part in site.parts) part.parms.threatPoints=200f;
                site.desiredThreatPoints=site.ActualThreatPoints;
                quest.acceptanceExpireTick=Find.TickManager.TicksGame+30*60000;
                var witness=quest.AddPart<QuestExpeditionWitness>();witness.inSignalEnable=quest.InitiateSignal;
                witness.home=home.Parent;witness.site=site;witness.crew=crew;
                Find.QuestManager.Add(quest);
                return new {success=true,questId=quest.GetUniqueLoadID(),scriptDef=script,siteId=site.GetUniqueLoadID(),homeMapId=home.uniqueID,homeTile=home.Tile.tileId,tile=tile.tileId,pawnIds=crew.Select(p=>p.GetUniqueLoadID()).ToArray(),shuttleMission};
            },cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_expedition_read", Description = "UNSAFE FOR MODEL EXECUTION. Read physical fixture caravan/shuttle departure, site fight, return and loot; never supplies the outcome.")]
        public async Task<object> ReadExpedition(IRimBridgeContext ctx,CancellationToken cancellationToken,string questId)
        {
            return await ctx.MainThread.InvokeAsync<object>(()=> {
                var quest=Find.QuestManager.QuestsListForReading.FirstOrDefault(q=>q.GetUniqueLoadID()==questId);
                var witness=quest?.PartsListForReading.OfType<QuestExpeditionWitness>().SingleOrDefault();
                if(witness==null) return Refuse("Expedition witness absent.");
                witness.Observe();
                var returned=witness.travelers.Count>0 && witness.travelers.All(p=>!p.Dead && p.Spawned && p.Map?.Parent==witness.home);
                var loot=witness.home.HasMap?witness.home.Map.listerThings.AllThings.Where(t=>t.def==ThingDefOf.Silver).Sum(t=>t.stackCount):0;
                loot+=witness.travelers.Where(p=>p.Spawned && p.Map?.Parent==witness.home).Sum(p=>p.inventory.innerContainer.Where(t=>t.def==ThingDefOf.Silver).Sum(t=>t.stackCount));
                var lootReturned=witness.siteLoot!=null && !witness.siteLoot.Destroyed && witness.siteLoot.MapHeld?.Parent==witness.home;
                return new {success=true,state=quest.State.ToString(),formed=witness.formed,boarded=witness.boarded,arrived=witness.arrived,cleared=witness.cleared,returned,loot,lootReturned,travelers=witness.travelers.Select(p=>p.GetUniqueLoadID()).ToArray()};
            },cancellationToken).ConfigureAwait(false);
        }

        private static Faction FixtureEmpire() {
            var empire=Find.FactionManager.FirstFactionOfDef(FactionDefOf.Empire);
            if(empire==null) { empire=FactionGenerator.NewGeneratedFaction(new FactionGeneratorParms(FactionDefOf.Empire));Find.FactionManager.Add(empire); }
            if(empire.HostileTo(Faction.OfPlayer)) empire.SetRelationDirect(Faction.OfPlayer,FactionRelationKind.Neutral,false);
            return empire;
        }
        [Tool("test/quest_hack_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable lab: an unhacked Ideology terminal and ordinary Research worker; no autohack designation, hacking job or completion supplied.")]
        public async Task<object> PrepareHack(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !ModsConfig.IdeologyActive) return Refuse("Ideology lab map is required.");
                var hacker = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Research) && !p.Dead && !p.Downed);
                if (hacker == null) return Refuse("The pinned lab needs a Research-capable colonist.");
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned) {
                    pawn.workSettings.EnableAndInitialize();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) pawn.workSettings.SetPriority(work, 0);
                    pawn.jobs.StopAll();
                    for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                }
                hacker.skills.GetSkill(SkillDefOf.Intellectual).Level = 12;
                hacker.workSettings.SetPriority(WorkTypeDefOf.Research, 1);
                var terminal = Place(map, DefDatabase<ThingDef>.GetNamed("AncientTerminal_Worshipful"), null, map.Center + new IntVec3(6, 0, 6));
                map.fogGrid.Unfog(terminal.Position);
                var item = ThingMaker.MakeThing(ThingDefOf.WoodLog); item.stackCount = 1;
                GenSpawn.Spawn(item, map.Center + new IntVec3(7, 0, 6), map);
                FixtureMeals(map);
                var hack = terminal.TryGetComp<CompHackable>();
                if (hack == null || hack.Autohack || hack.IsHacked || hack.ProgressPercent != 0) return Refuse("The terminal must start untouched.");
                return new { success = true, terminalId = terminal.GetUniqueLoadID(), hackerId = hacker.GetUniqueLoadID(), unhackableItemId = item.GetUniqueLoadID() };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_hack_read", Description = "UNSAFE FOR MODEL EXECUTION. Read exact terminal autohack, actual native hacking progress/completion and the vanilla comp's last hacker; never changes progress.")]
        public async Task<object> ReadHack(IRimBridgeContext ctx, CancellationToken cancellationToken, string terminalId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var terminal = map?.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == terminalId);
                var hack = terminal?.TryGetComp<CompHackable>();
                if (hack == null) return Refuse("The exact terminal is unavailable.");
                var hacker = (Pawn)typeof(CompHackable).GetField("lastUser", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(hack);
                return new { success = true, tick = Find.TickManager.TicksGame, autohack = hack.Autohack, hacked = hack.IsHacked,
                    progress = hack.ProgressPercent, hackerId = hacker?.GetUniqueLoadID() ?? "" };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_give_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable Ideology lab with a vanilla beggar lord requesting twenty silver; no delivery job or received items supplied.")]
        public async Task<object> PrepareGive(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !ModsConfig.IdeologyActive) return Refuse("Ideology lab map is required.");
                var hauler = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .FirstOrDefault(p => !p.Dead && !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                var faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && !f.def.hidden && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer));
                if (hauler == null || faction == null) return Refuse("A mobile hauler and neutral visitor faction are required.");
                foreach (var pawn in map.mapPawns.FreeColonistsSpawned) {
                    pawn.workSettings.EnableAndInitialize();
                    foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) pawn.workSettings.SetPriority(work, 0);
                    pawn.jobs.StopAll();
                    for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                }
                var recipient = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, faction);
                recipient.inventory.innerContainer.ClearAndDestroyContents();
                var spot = map.Center + new IntVec3(5, 0, 5);
                GenSpawn.Spawn(recipient, spot, map);
                Verse.AI.Group.LordMaker.MakeNewLord(faction, new LordJob_BegForItems(faction, spot, recipient, ThingDefOf.Silver, 20), map, new[] { recipient });
                var silver = ThingMaker.MakeThing(ThingDefOf.Silver); silver.stackCount = 20;
                GenSpawn.Spawn(silver, map.Center + new IntVec3(2, 0, 5), map);
                map.fogGrid.Unfog(spot); map.fogGrid.Unfog(silver.Position);
                FixtureMeals(map);
                return new { success = true, haulerId = hauler.GetUniqueLoadID(), recipientId = recipient.GetUniqueLoadID(), remaining = GiveItemsToPawnUtility.ItemCountLeftToCollect(recipient) };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_give_read", Description = "UNSAFE FOR MODEL EXECUTION. Read actual recipient silver inventory and native whole-request remainder, plus the exact hauler's current job; no delivery mutations.")]
        public async Task<object> ReadGive(IRimBridgeContext ctx, CancellationToken cancellationToken, string recipientId, string haulerId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var recipient = Find.Maps.SelectMany(m => m.mapPawns.AllPawns)
                    .Concat(Find.WorldPawns.AllPawnsAliveOrDead).FirstOrDefault(p => p.GetUniqueLoadID() == recipientId);
                var hauler = map?.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == haulerId);
                if (recipient == null || hauler == null) return Refuse("Exact lab pawns are unavailable.");
                return new { success = true, tick = Find.TickManager.TicksGame,
                    received = recipient.inventory.innerContainer.Where(t => t.def == ThingDefOf.Silver).Sum(t => t.stackCount),
                    remaining = GiveItemsToPawnUtility.GetCountRemaining(recipient, ThingDefOf.Silver, 20),
                    job = hauler.CurJob?.def.defName ?? "", targetId = hauler.CurJob?.targetB.Pawn?.GetUniqueLoadID() ?? "" };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static void FixtureMeals(Map map) {
            for(var i=0;i<4;i++) { var food=ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);food.stackCount=food.def.stackLimit;GenSpawn.Spawn(food,map.Center+new IntVec3(i,0,4),map); }
        }

        private static Thing Place(Map map, ThingDef def, ThingDef stuff, IntVec3 cell)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(thing, cell, map, Rot4.North);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }

    public sealed class QuestHospitalityWitness : QuestPartActivable
    {
        public Pawn pawn; public Building_Bed bed; public bool hosted,assigned,boarded;
        public override void QuestPartTick() {
            if(pawn==null) return;
            hosted|=pawn.Spawned && pawn.HasExtraHomeFaction(quest);
            assigned|=bed!=null && bed.OwnersForReading.Contains(pawn);
            boarded|=Find.Maps.SelectMany(m=>m.listerThings.AllThings).Select(t=>t.TryGetComp<CompTransporter>()).Any(t=>t!=null && t.innerContainer.Contains(pawn));
        }
        public override void ExposeData() { base.ExposeData();Scribe_References.Look(ref pawn,"pawn");Scribe_References.Look(ref bed,"bed");Scribe_Values.Look(ref hosted,"hosted");Scribe_Values.Look(ref assigned,"assigned");Scribe_Values.Look(ref boarded,"boarded"); }
    }

    public sealed class QuestExpeditionWitness : QuestPartActivable
    {
        public MapParent home;public Site site;public List<Pawn> crew=new List<Pawn>();public List<Pawn> travelers=new List<Pawn>();
        public bool formed,boarded,arrived,cleared,lootStaged;
        public Thing siteLoot;
        public void Observe() {
            foreach(var pawn in crew) {
                var inCaravan=Find.WorldObjects.Caravans.Any(c=>c.IsPlayerControlled && c.PawnsListForReading.Contains(pawn));
                var inShuttle=Find.Maps.SelectMany(m=>m.listerThings.AllThings).Select(t=>t.TryGetComp<CompTransporter>()).Any(c=>c!=null && c.innerContainer.Contains(pawn));
                var atSite=pawn.Spawned && pawn.Map?.Parent==site;
                formed|=inCaravan;boarded|=inShuttle;arrived|=atSite;
                if((inCaravan||inShuttle||atSite) && !travelers.Contains(pawn)) travelers.Add(pawn);
            }
            if(arrived && site.HasMap) cleared|=!GenHostility.AnyHostileActiveThreatToPlayer(site.Map,countDormantPawnsAsHostile:true,canBeFogged:true);
        }
        public override void QuestPartTick() {
            if(!lootStaged && site.HasMap) {
                var map=site.Map;
                var cell=GenRadial.RadialCellsAround(map.Center,12,true).First(c=>c.InBounds(map) && c.Standable(map) && c.GetFirstItem(map)==null);
                var silver=ThingMaker.MakeThing(ThingDefOf.Silver);silver.stackCount=30;GenSpawn.Spawn(silver,cell,map);silver.SetForbidden(false,false);siteLoot=silver;
                lootStaged=true;
            }
            Observe();
        }
        public override void Notify_QuestSignalReceived(Signal signal) {
            base.Notify_QuestSignalReceived(signal);
            if(signal.tag.EndsWith(".AllEnemiesDefeated",StringComparison.Ordinal)) cleared=true;
        }
        public override void ExposeData() {
            base.ExposeData();Scribe_References.Look(ref home,"home");Scribe_References.Look(ref site,"site");
            Scribe_References.Look(ref siteLoot,"siteLoot");
            Scribe_Collections.Look(ref crew,"crew",LookMode.Reference);Scribe_Collections.Look(ref travelers,"travelers",LookMode.Reference);
            Scribe_Values.Look(ref formed,"formed");Scribe_Values.Look(ref boarded,"boarded");Scribe_Values.Look(ref arrived,"arrived");Scribe_Values.Look(ref cleared,"cleared");Scribe_Values.Look(ref lootStaged,"lootStaged");
        }
    }
}
