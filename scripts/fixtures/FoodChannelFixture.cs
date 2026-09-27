using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using HarmonyLib;

namespace HomeBridge.BridgeTools
{
    // Disposable Core-only initial state. Cases add their channel after this
    // reset; nothing here advances time or suppresses ordinary simulation.
    public sealed class FoodChannelFixture
    {
        [Tool("test/food_baseline_prey", Description = "UNSAFE FOR MODEL EXECUTION. Add one deterministic wild deer on reachable ground near the paused tribal baseline. Preserve all existing stock, plants, zones, animals, pawn skills, equipment and work settings; the controller supplies hunting prerequisites.")]
        public async Task<object> BaselinePrey(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("Paused disposable map required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).ToList();
                if (people.Count == 0) throw new InvalidOperationException("Standing colonist required.");
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var cells = GenRadial.RadialCellsAround(center, 20, true).Where(c => c.InBounds(map)
                    && c.DistanceToSquared(center) >= 100 && !c.Fogged(map) && !c.Roofed(map)
                    && c.Standable(map) && c.GetEdifice(map) == null
                    && people.Any(p => p.CanReach(c, PathEndMode.OnCell, Danger.None))).Take(1).ToList();
                if (cells.Count == 0) throw new InvalidOperationException("No safe reachable prey cell.");
                Pawn deer;
                Rand.PushState(419);
                try { deer = PawnGenerator.GeneratePawn(PawnKindDef.Named("Deer"), null); }
                finally { Rand.PopState(); }
                GenSpawn.Spawn(deer, cells[0], map);
                deer.SetForbidden(false, false);
                return new { success = true, tick = Find.TickManager.TicksGame, prey = deer.GetUniqueLoadID(),
                    kind = deer.def.defName, wild = deer.Faction == null, x = deer.Position.x, z = deer.Position.z };
            }, cancellationToken);
        }

        private static Game humanGame;
        private static string humanCook;
        private static readonly HashSet<string> fedHerd = new HashSet<string>();
        private static bool ingestPatched;
        private static void HumanIngested(Thing __instance, Pawn ingester, float __result)
        {
            if (ReferenceEquals(humanGame, Current.Game) && __result > 0 && ingester.RaceProps.Animal && __instance.def.defName == "Kibble")
                fedHerd.Add(ingester.GetUniqueLoadID());
        }

        [Tool("test/human_butchery_prepare", Description="UNSAFE FOR MODEL EXECUTION. Add six raider corpses, one psychopath cook, two hungry huskies, vegetable stock, a stove and butcher spot, and an empty screened corpse room to EmptyChannels. No human butcher or meal bill is preinstalled.")]
        public async Task<object> HumanPrepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(()=>{
                var map=Find.CurrentMap??throw new InvalidOperationException("Loaded map required");
                if(!Find.TickManager.Paused)throw new InvalidOperationException("Paused fixture required");
                var people=map.mapPawns.FreeColonistsSpawned.ToList();
                var cooking=DefDatabase<WorkTypeDef>.GetNamed("Cooking");
                var cook=people.First(p=>!p.WorkTypeIsDisabled(cooking)&&!p.Downed);
                var anchor=GenRadial.RadialCellsAround(cook.Position,25,true).First(c=>CellRect.CenteredOn(c,25,15).Cells.All(x=>x.InBounds(map)&&!x.Fogged(map)&&x.GetTerrain(map).passability!=Traversability.Impassable));
                foreach(var cell in CellRect.CenteredOn(anchor,25,15).Cells) {
                    foreach(var thing in cell.GetThingList(map).ToList())if(!(thing is Pawn))thing.Destroy(DestroyMode.Vanish);
                    map.terrainGrid.SetTerrain(cell,TerrainDefOf.Concrete);
                }
                foreach(var pawn in people) {
                    pawn.jobs.StopAll();pawn.drafter.Drafted=false;
                    foreach(var trait in pawn.story.traits.allTraits.Where(t=>t.def.defName=="Psychopath"||t.def.defName=="Bloodlust"||t.def.defName=="Cannibal").ToList())pawn.story.traits.RemoveTrait(trait);
                    foreach(var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)if(!pawn.WorkTypeIsDisabled(work))pawn.workSettings.SetPriority(work,0);
                    if(!pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))pawn.workSettings.SetPriority(WorkTypeDefOf.Hauling,2);
                    pawn.Position=anchor+new IntVec3(-5,0,0);
                    for(var hour=0;hour<24;hour++)pawn.timetable.SetAssignment(hour,TimeAssignmentDefOf.Work);
                }
                cook.story.traits.GainTrait(new Trait(TraitDefOf.Psychopath));cook.skills.GetSkill(SkillDefOf.Cooking).Level=20;cook.workSettings.SetPriority(cooking,1);
                Thing Spawn(string name,IntVec3 cell) {var def=ThingDef.Named(name);var thing=ThingMaker.MakeThing(def,def.MadeFromStuff?ThingDefOf.WoodLog:null);if(def.CanHaveFaction)thing.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(thing,cell,map);thing.SetForbidden(false,false);return thing;}
                var bench=(Building_WorkTable)Spawn("ButcherSpot",anchor);
                var animalBill=(Bill_Production)DefDatabase<RecipeDef>.GetNamed("ButcherCorpseFlesh").MakeNewBill();animalBill.repeatMode=BillRepeatModeDefOf.Forever;
                foreach(var def in DefDatabase<ThingDef>.AllDefsListForReading.Where(d=>d.IsCorpse&&d.ingestible?.sourceDef?.race?.Humanlike==true))animalBill.ingredientFilter.SetAllow(def,false);
                bench.BillStack.AddBill(animalBill);
                var stove=(Building_WorkTable)Spawn("FueledStove",anchor+new IntVec3(4,0,0));stove.TryGetComp<CompRefuelable>().Refuel(100);
                foreach(var recipe in stove.def.AllRecipes)if(recipe.products.Any(p=>p.thingDef.defName=="MealSurvivalPack")){if(recipe.researchPrerequisite!=null)Find.ResearchManager.FinishProject(recipe.researchPrerequisite,false);foreach(var research in recipe.researchPrerequisites??new List<ResearchProjectDef>())Find.ResearchManager.FinishProject(research,false);}
                var room=new CellRect(anchor.x+7,anchor.z-2,5,5);
                foreach(var cell in room.Cells) {map.roofGrid.SetRoof(cell,RoofDefOf.RoofConstructed);if(cell.x==room.minX||cell.x==room.maxX||cell.z==room.minZ||cell.z==room.maxZ)Spawn(cell==new IntVec3(room.minX,0,room.minZ+2)?"Door":"Wall",cell);}
                for(var i=0;i<6;i++) {var raider=PawnGenerator.GeneratePawn(PawnKindDefOf.SpaceRefugee,Faction.OfAncientsHostile);GenSpawn.Spawn(raider,anchor+new IntVec3(i,0,-3),map);raider.Kill(null);raider.Corpse.SetForbidden(false,false);}
                for(var i=0;i<7;i++){var rice=Spawn("RawRice",anchor+new IntVec3(i,0,3));rice.stackCount=75;}
                var feedBench=Spawn("ButcherSpot",anchor+new IntVec3(0,0,6));
                if(!map.areaManager.TryMakeNewAllowed(out var feedArea))throw new InvalidOperationException("No feed area");foreach(var cell in CellRect.CenteredOn(feedBench.Position,5,3).Cells)feedArea[cell]=true;
                var herd=new List<Pawn>();for(var i=0;i<2;i++){var animal=PawnGenerator.GeneratePawn(PawnKindDef.Named("Husky"),Faction.OfPlayer);GenSpawn.Spawn(animal,anchor+new IntVec3(0,0,5+i),map);animal.playerSettings.AreaRestrictionInPawnCurrentMap=feedArea;animal.needs.food.CurLevelPercentage=0.15f;herd.Add(animal);}
                humanGame=Current.Game;humanCook=cook.GetUniqueLoadID();fedHerd.Clear();
                if(!ingestPatched){new Harmony("rimgovernor.fixture.humanfood").Patch(AccessTools.Method(typeof(Thing),"Ingested"),postfix:new HarmonyMethod(typeof(FoodChannelFixture),nameof(HumanIngested)));ingestPatched=true;}
                return new {success=true,cook=humanCook,bench=bench.GetUniqueLoadID(),stove=stove.GetUniqueLoadID(),feedBench=feedBench.GetUniqueLoadID(),herd=herd.Select(p=>p.GetUniqueLoadID()).ToArray(),corpses=6};
            },cancellationToken);
        }

        [Tool("test/human_butchery_observe", Description="Read pinned human bills, actual herd kibble ingestion, protected human survival-meal stock and personal butchery thoughts. No mutations.")]
        public async Task<object> HumanObserve(IRimBridgeContext ctx,CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(()=>{
                var map=Find.CurrentMap??throw new InvalidOperationException("Loaded map required");
                var bills=map.listerThings.AllThings.OfType<Building_WorkTable>().SelectMany(b=>b.BillStack.Bills).OfType<Bill_Production>().ToList();
                bool Human(ThingDef d)=>FoodUtility.GetMeatSourceCategory(d)==MeatSourceCategory.Humanlike;
                bool HumanCorpse(ThingDef d)=>d.IsCorpse&&d.ingestible?.sourceDef?.race?.Humanlike==true;
                var human=bills.Where(b=>b.recipe.defName=="ButcherCorpseFlesh"&&b.ingredientFilter.AllowedThingDefs.Any(HumanCorpse)).ToList();
                var cook=map.mapPawns.FreeColonistsSpawned.Single(p=>p.GetUniqueLoadID()==humanCook);
                return new {success=true,corpses=map.listerThings.AllThings.OfType<Corpse>().Count(c=>c.InnerPawn.RaceProps.Humanlike),
                    pinned=human.Count==1&&human[0].PawnRestriction==cook,animalBill=bills.Any(b=>b.recipe.defName=="ButcherCorpseFlesh"&&!b.ingredientFilter.AllowedThingDefs.Any(HumanCorpse)),
                    unsafeMealBill=bills.Any(b=>b.recipe.products.Any(p=>p.thingDef.IsNutritionGivingIngestible&&p.thingDef.defName!="Kibble"&&p.thingDef.defName!="MealSurvivalPack")&&b.ingredientFilter.AllowedThingDefs.Any(Human)),
                    fedHerd=fedHerd.ToArray(),personalPenalty=cook.needs.mood.thoughts.memories.Memories.Any(m=>m.def.defName=="ButcheredHumanlikeCorpse"&&m.MoodOffset()<0),
                    tradeMeals=map.listerThings.AllThings.Where(t=>t.def.defName=="MealSurvivalPack"&&t.IsForbidden(Faction.OfPlayer)&&t.TryGetComp<CompIngredients>()?.ingredients.Any(Human)==true).Sum(t=>t.stackCount)};
            },cancellationToken);
        }
        [Tool("test/food_channels_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Strip food stocks (including held food), crops, growing zones, animals, corpses and food-producing buildings from a paused disposable Core map. Seed exactly stockUnits of foodDef. Channel cases add only the source they prove afterwards.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            int stockUnits = 0, string foodDef = "MealSurvivalPack")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var timer = Stopwatch.StartNew();
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("Paused disposable map required.");
                if (ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageId.StartsWith("ludeon.rimworld.", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Food channel fixture requires Core only.");
                if (stockUnits < 0 || stockUnits > 1000) throw new ArgumentException("stockUnits must be 0..1000.");
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(foodDef);
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0 || def == null || !def.IsNutritionGivingIngestible || def.IsDrug
                    || def.ingestible == null || (def.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) != 0
                    || people.Any(p => !p.WillEat(def)))
                    throw new ArgumentException("Need colonists and human food all colonists can eat.");
                var anchor = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var cells = GenRadial.RadialCellsAround(anchor, 20, true).Where(c => c.InBounds(map) && !c.Fogged(map)
                    && c.Standable(map) && c.GetEdifice(map) == null
                    && people.Any(p => !p.Downed && p.CanReach(c, PathEndMode.OnCell, Danger.None))).Take(stockUnits).ToList();
                var stacks = (stockUnits + def.stackLimit - 1) / def.stackLimit;
                if (cells.Count < stacks) throw new InvalidOperationException("No reachable space for declared stock.");
                // Cancel jobs before removing carried ingredients and targets.
                foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList()) pawn.jobs?.StopAll();
                foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>().ToList()) zone.Delete();
                foreach (var thing in AllThings(map).Where(Remove).ToList())
                    if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);
                var placed = new List<Thing>();
                for (var remaining = stockUnits; remaining > 0; remaining -= def.stackLimit)
                {
                    var food = ThingMaker.MakeThing(def);
                    food.stackCount = Math.Min(remaining, def.stackLimit);
                    GenSpawn.Spawn(food, cells[placed.Count], map);
                    food.SetForbidden(false, false);
                    placed.Add(food);
                }
                return new { success = true, stockUnits, foodDef,
                    declaredNutrition = placed.Sum(t => (double)t.stackCount * people.Min(p => FoodUtility.NutritionForEater(p, t))),
                    preparationMs = timer.ElapsedMilliseconds, anchor = new { x = anchor.x, z = anchor.z } };
            }, cancellationToken);
        }

        [Tool("test/food_channels_observe", Description = "Read remaining food channels and held food in the disposable fixture; no mutations.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("Loaded map required.");
                var things = AllThings(map).Where(t => !t.Destroyed).ToList();
                return new { success = true,
                    fields = map.zoneManager.AllZones.OfType<Zone_Growing>().Count(),
                    plants = things.OfType<Plant>().Count(FoodPlant),
                    animals = things.OfType<Pawn>().Count(p => p.RaceProps.Animal),
                    heldAnimals = things.OfType<Pawn>().Where(p => p.RaceProps.Animal && !p.Spawned).Select(p => new { defName = p.def.defName, holder = Holders(p) }).ToList(),
                    corpses = things.OfType<Corpse>().Count(),
                    producers = things.Count(Producer),
                    stock = things.Where(t => t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible)
                        .Select(t => new { defName = t.def.defName, units = t.stackCount, spawned = t.Spawned, holder = t.Spawned ? null : Holders(t) }).ToList() };
            }, cancellationToken);
        }

        [Tool("test/meal_tiers_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Add a skilled assigned cook, cow, fueled stove and raw rice/milk surplus to the empty-channel fixture.")]
        public async Task<object> Meals(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required");
                var people = map.mapPawns.FreeColonistsSpawned.ToList();
                var pawn = people.First();
                var cells = GenRadial.RadialCellsAround(pawn.Position, 15, true).Where(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map)==null).ToList();
                var center = cells.First(c => GenAdj.OccupiedRect(c, Rot4.North, new IntVec2(3,3)).Cells.All(x=>x.InBounds(map)&&x.Standable(map)&&x.GetEdifice(map)==null));
                foreach(var p in people) { p.jobs.StopAll(); p.workSettings.EnableAndInitialize(); p.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking"), 1); p.skills.GetSkill(SkillDefOf.Cooking).Level=8; }
                var stove=(Building_WorkTable)ThingMaker.MakeThing(ThingDef.Named("FueledStove"));stove.SetFaction(Faction.OfPlayer);GenSpawn.Spawn(stove,center,map);stove.TryGetComp<CompRefuelable>().Refuel(50);
                var cow=PawnGenerator.GeneratePawn(PawnKindDef.Named("Cow"),Faction.OfPlayer);GenSpawn.Spawn(cow,cells.Last(),map);
                int index=0;
                foreach(var name in new[]{"RawRice","Milk"}) {
                    var def=ThingDef.Named(name);int remaining=name=="RawRice"?10000:1000;
                    while(remaining>0){var cell=cells[index++];if(cell.GetEdifice(map)!=null)continue;var food=ThingMaker.MakeThing(def);food.stackCount=Math.Min(def.stackLimit,remaining);remaining-=food.stackCount;GenSpawn.Spawn(food,cell,map);food.SetForbidden(false,false);}
                }
                return new {success=true,bench=stove.GetUniqueLoadID(),spareCell=new{x=cells.Last().x,z=cells.Last().z}};
            },cancellationToken);
        }

        [Tool("test/meal_tiers_probe", Description = "UNSAFE FOR MODEL EXECUTION when drain is true. Read native bill IDs; optionally reduce raw rice and milk to a small fallback stock without changing bills.")]
        public async Task<object> MealProbe(IRimBridgeContext ctx,CancellationToken cancellationToken,bool drain=false)
        {
            return await ctx.MainThread.InvokeAsync<object>(()=>{
                var map=Find.CurrentMap ?? throw new InvalidOperationException("Map required");
                if(drain){if(!Find.TickManager.Paused)throw new InvalidOperationException("Pause before drain");foreach(var p in map.mapPawns.FreeColonistsSpawned)p.jobs.StopAll();
                    foreach(var name in new[]{"RawRice","Milk"}){var rows=AllThings(map).Where(t=>!t.Destroyed&&t.def.defName==name).ToList();foreach(var t in rows)t.Destroy(DestroyMode.Vanish);var food=ThingMaker.MakeThing(ThingDef.Named(name));food.stackCount=20;GenSpawn.Spawn(food,map.mapPawns.FreeColonistsSpawned.First().Position,map);}
                }
                return new {success=true,bills=map.listerThings.AllThings.OfType<Building_WorkTable>().SelectMany(b=>b.BillStack.Bills).Select(b=>new{id=b.GetUniqueLoadID(),recipe=b.recipe.defName}).ToList()};
            },cancellationToken);
        }

        private static float reserveEaten;
        private static bool reservePatched;
        private static void ReserveIngested(Thing __instance, Pawn ingester, float __result)
        {
            if (__instance.def.defName == "Pemmican" && ingester?.IsColonist == true) reserveEaten += __result;
        }

        [Tool("test/food_reserve_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Add a roofed walled food room with a fueled stove, a food stockpile, pemmican research, unforbidden pemmican and meat, and a 10000 raw rice runway and 1000 wood outside it to the empty-channel fixture; every colonist cooks. Nothing is forbidden and no bill is preinstalled.")]
        public async Task<object> ReservePrepare(IRimBridgeContext ctx, CancellationToken cancellationToken, int pemmican = 150)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required");
                if (pemmican < 0 || pemmican > 1000) throw new ArgumentException("pemmican must be 0..1000");
                var people = map.mapPawns.FreeColonistsSpawned.ToList();
                var cook = people.First(p => !p.Downed);
                var room = CellRect.Empty;
                foreach (var c in GenRadial.RadialCellsAround(cook.Position, 25, true)) {
                    var rect = CellRect.CenteredOn(c, 11, 9);
                    if (rect.Cells.All(x => x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetEdifice(map) == null && x.GetTerrain(map).passability != Traversability.Impassable && !x.GetThingList(map).Any(t => t is Pawn))) { room = rect; break; }
                }
                if (room.IsEmpty) throw new InvalidOperationException("No clear ground for the food room");
                foreach (var p in people) { p.jobs.StopAll(); p.workSettings.EnableAndInitialize(); p.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking"), 1); if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2); p.skills.GetSkill(SkillDefOf.Cooking).Level = 8; }
                Thing Spawn(string name, IntVec3 cell) { var def = ThingDef.Named(name); var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null); if (def.CanHaveFaction) thing.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(thing, cell, map); thing.SetForbidden(false, false); return thing; }
                var door = new IntVec3(room.minX, 0, room.CenterCell.z);
                foreach (var cell in room.Cells) {
                    foreach (var thing in cell.GetThingList(map).ToList()) if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    if (cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ) Spawn(cell == door ? "Door" : "Wall", cell);
                }
                var inside = room.ContractedBy(1);
                var stove = (Building_WorkTable)Spawn("FueledStove", new IntVec3(inside.maxX - 1, 0, inside.CenterCell.z));
                stove.TryGetComp<CompRefuelable>().Refuel(100);
                foreach (var recipe in stove.def.AllRecipes) if (recipe.products.Any(p => p.thingDef.defName == "Pemmican")) { if (recipe.researchPrerequisite != null) Find.ResearchManager.FinishProject(recipe.researchPrerequisite, false); foreach (var research in recipe.researchPrerequisites ?? new List<ResearchProjectDef>()) Find.ResearchManager.FinishProject(research, false); }
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                var free = new List<IntVec3>();
                foreach (var cell in inside.Cells) { if (cell.GetEdifice(map) != null || stove.OccupiedRect().Contains(cell) || stove.InteractionCell == cell) continue; zone.AddCell(cell); free.Add(cell); }
                zone.settings.filter.SetDisallowAll(); zone.settings.filter.SetAllow(ThingCategoryDefOf.Foods, true); zone.settings.Priority = StoragePriority.Important;
                int index = 0;
                // Rice is the long-lived runway (40 rot days) that keeps the review out of
                    // emergency under the seasonal minimum; it needs no roof, so it sits outside.
                    // Wood keeps the stove fueled: the meal bill alone burns the initial 100 fuel.
                    var outside = GenRadial.RadialCellsAround(room.CenterCell, 30, true).Where(c => c.InBounds(map) && !room.Contains(c) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)).ToList();
                    foreach (var seed in new[] { ("Pemmican", pemmican, free), ("Meat_Muffalo", 600, free), ("RawRice", 10000, outside), ("WoodLog", 1000, outside) }) {
                        var def = ThingDef.Named(seed.Item1); int remaining = seed.Item2; var cells = seed.Item3; if (cells == outside) index = 0;
                        while (remaining > 0) { var food = ThingMaker.MakeThing(def); food.stackCount = Math.Min(def.stackLimit, remaining); remaining -= food.stackCount; GenSpawn.Spawn(food, cells[index++], map); food.SetForbidden(false, false); }
                    }
                reserveEaten = 0;
                if (!reservePatched) { new Harmony("rimgovernor.fixture.foodreserve").Patch(AccessTools.Method(typeof(Thing), "Ingested"), postfix: new HarmonyMethod(typeof(FoodChannelFixture), nameof(ReserveIngested))); reservePatched = true; }
                return new { success = true, bench = stove.GetUniqueLoadID(), pemmican, room = new { x = room.minX, z = room.minZ, w = room.Width, h = room.Height } };
            }, cancellationToken);
        }

        [Tool("test/food_reserve_probe", Description = "UNSAFE FOR MODEL EXECUTION when drain is true. Read pemmican stacks, food bills and colonist pemmican ingestion; optionally destroy every other food (free pemmican included), reset the ingestion counter and leave the colonists hungry.")]
        public async Task<object> ReserveProbe(IRimBridgeContext ctx, CancellationToken cancellationToken, bool drain = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("Map required");
                if (drain) {
                    if (!Find.TickManager.Paused) throw new InvalidOperationException("Pause before drain");
                    foreach (var p in map.mapPawns.FreeColonistsSpawned) { p.jobs.StopAll(); p.needs.food.CurLevelPercentage = 0.2f; }
                    foreach (var t in AllThings(map).Where(t => !t.Destroyed && !(t is Pawn) && t.def.IsNutritionGivingIngestible && (t.def.defName != "Pemmican" || !t.IsForbidden(Faction.OfPlayer))).ToList()) t.Destroy(DestroyMode.Vanish);
                    reserveEaten = 0; // only the held reserve remains; count what the colonists eat from here
                }
                var things = AllThings(map).Where(t => !t.Destroyed).ToList();
                return new { success = true, reserveEaten,
                    pemmican = things.Where(t => t.def.defName == "Pemmican").Select(t => new { id = t.GetUniqueLoadID(), units = t.stackCount, forbidden = t.Spawned && t.IsForbidden(Faction.OfPlayer), roofed = t.Spawned && t.Position.Roofed(map) }).ToList(),
                    otherFood = things.Where(t => !(t is Pawn) && t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible && t.def.defName != "Pemmican").Sum(t => t.stackCount),
                    hungriest = map.mapPawns.FreeColonistsSpawned.Min(p => p.needs.food.CurLevelPercentage),
                    bills = map.listerThings.AllThings.OfType<Building_WorkTable>().SelectMany(b => b.BillStack.Bills).OfType<Bill_Production>().Select(b => new { id = b.GetUniqueLoadID(), recipe = b.recipe.defName, target = b.targetCount, repeat = b.repeatMode.defName, suspended = b.suspended, paused = b.paused, shouldDo = b.ShouldDoNow(), count = b.recipe.WorkerCounter.CountProducts(b), fuel = (b.billStack.billGiver as Thing)?.TryGetComp<CompRefuelable>()?.Fuel ?? -1f }).ToList(),
                    jobs = map.mapPawns.FreeColonistsSpawned.Select(p => new { name = p.LabelShort, job = p.CurJob?.def.defName ?? "", target = p.CurJob?.targetA.Thing?.def.defName ?? "", bill = p.CurJob?.bill?.recipe.defName ?? "", cooking = p.workSettings.GetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking")), hauling = p.workSettings.GetPriority(WorkTypeDefOf.Hauling) }).ToList() };
            }, cancellationToken);
        }

        private static bool FoodPlant(Plant p) => p.def.plant?.harvestedThingDef?.IsNutritionGivingIngestible == true;
        private static bool Producer(Thing t) => t is Building_PlantGrower || t is Building_NutrientPasteDispenser;
        private static bool Remove(Thing t) => t is Corpse || t is Pawn p && p.RaceProps.Animal
            || t is Plant plant && FoodPlant(plant) || Producer(t)
            || t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible;

        // The parent holder chain of an unspawned thing, outermost last.
        private static string Holders(Thing t)
        {
            var names = new List<string>();
            for (var holder = t.ParentHolder; holder != null && names.Count < 8; holder = holder.ParentHolder)
                names.Add(holder is Thing thing ? thing.GetUniqueLoadID() : holder.GetType().Name);
            return string.Join(" < ", names);
        }

        private static HashSet<Thing> AllThings(Map map)
        {
            var things = new HashSet<Thing>(map.listerThings.AllThings);
            var holders = new HashSet<IThingHolder>();
            void Visit(IThingHolder holder)
            {
                // Sealed ancient-danger caskets (megascarabs, a corpse, a
                // sleeper's pemmican) are no channel the colony can draw on.
                if (holder == null || holder is Building_AncientCryptosleepCasket || !holders.Add(holder)) return;
                var direct = holder.GetDirectlyHeldThings();
                if (direct != null) foreach (var thing in direct) things.Add(thing);
                var children = new List<IThingHolder>();
                holder.GetChildHolders(children);
                foreach (var child in children) Visit(child);
            }
            foreach (var thing in things.ToArray()) if (thing is IThingHolder holder) Visit(holder);
            return things;
        }
    }
}
