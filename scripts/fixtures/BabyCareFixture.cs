using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only: a colony raises
    // its first child. test/baby_care_prepare stages one baby a day or two short
    // of its first childhood birthday in a colony with no breastfeeder, and the
    // only baby-edible food the colony can get is what its stove cooks: a
    // walled, roofed kitchen with a fueled stove, the ingredients of the stove's
    // baby-edible recipe and that recipe's research. Which recipe, ingredients
    // and birthday age apply are read from the game's defs, never named here.
    // Everything after the staging (the baby food bill, the cooking, the
    // feeding and the growth) is the game's and the controller's.
    public sealed class BabyCareFixture
    {
        private static WorkTypeDef Cooking => DefDatabase<WorkTypeDef>.GetNamed("Cooking");
        private static bool BabyEdible(ThingDef def) => def?.ingestible != null && def.ingestible.babiesCanIngest;

        [Tool("test/baby_care_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#1691): build a roofed kitchen with a fueled stove, seed the ingredients of its baby-edible recipe, finish the recipe's research and add one baby daysToChild days short of its first childhood stage, with no breastfeeder in the colony. Requires Biotech.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Game days before the baby reaches the Child developmental stage (0.5..10).")] double daysToChild = 2)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.BiotechActive) return Refuse("Biotech is not active.");
                if (!(daysToChild >= 0.5 && daysToChild <= 10)) return Refuse("daysToChild must be within 0.5..10.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).OrderBy(p => p.thingIDNumber).ToList();
                var cook = colonists.FirstOrDefault(p => !p.WorkTypeIsDisabled(Cooking));
                if (cook == null) return Refuse("A colonist able to cook is required.");
                if (!colonists.Any(p => p.DevelopmentalStage.Adult() && !p.WorkTypeIsDisabled(WorkTypeDefOf.Childcare)))
                    return Refuse("An adult colonist able to do childcare is required.");
                if (ChildcareUtility.CanBreastfeedPlayerPawns.Any()) return Refuse("A colonist can already breastfeed; the case needs bottle feeding.");

                // The kitchen: clear ground near the cook, walled and roofed, the
                // stove on its far wall.
                var room = CellRect.Empty;
                foreach (var c in GenRadial.RadialCellsAround(cook.Position, 25, true)) {
                    var rect = CellRect.CenteredOn(c, 9, 7);
                    if (rect.Cells.All(x => x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetEdifice(map) == null
                        && x.GetTerrain(map).passability != Traversability.Impassable && !x.GetThingList(map).Any(t => t is Pawn))) { room = rect; break; }
                }
                if (room.IsEmpty) return Refuse("No clear ground for the kitchen.");
                Thing Spawn(ThingDef def, IntVec3 cell) {
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    if (def.CanHaveFaction) thing.SetFaction(player);
                    GenSpawn.Spawn(thing, cell, map);
                    thing.SetForbidden(false, false);
                    return thing;
                }
                var door = new IntVec3(room.minX, 0, room.CenterCell.z);
                foreach (var cell in room.Cells) {
                    foreach (var thing in cell.GetThingList(map).ToList()) if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    if (cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ)
                        Spawn(cell == door ? ThingDefOf.Door : ThingDefOf.Wall, cell);
                }
                var inside = room.ContractedBy(1);
                var stove = (Building_WorkTable)Spawn(ThingDef.Named("FueledStove"), new IntVec3(inside.maxX, 0, inside.CenterCell.z));
                stove.TryGetComp<CompRefuelable>().Refuel(100);

                // The recipe: the stove's one whose single product a baby can eat.
                var recipe = stove.def.AllRecipes.FirstOrDefault(r => r.products.Count == 1 && BabyEdible(r.products[0].thingDef));
                if (recipe == null) return Refuse("The stove has no baby-edible recipe.");
                if (recipe.researchPrerequisite != null) Find.ResearchManager.FinishProject(recipe.researchPrerequisite, false);
                foreach (var research in recipe.researchPrerequisites ?? new List<ResearchProjectDef>()) Find.ResearchManager.FinishProject(research, false);

                // A stockpile over the kitchen floor holds the ingredients. Each
                // ingredient is the first food its filter allows that the colony
                // can eat; the stock is forty batches, far past one day's need.
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                var free = new List<IntVec3>();
                foreach (var cell in inside.Cells) {
                    if (cell.GetEdifice(map) != null || stove.OccupiedRect().Contains(cell) || stove.InteractionCell == cell) continue;
                    zone.AddCell(cell); free.Add(cell);
                }
                zone.settings.filter.SetDisallowAll(); zone.settings.filter.SetAllow(ThingCategoryDefOf.Foods, true);
                zone.settings.Priority = StoragePriority.Important;
                var seeded = new List<object>(); var next = 0;
                foreach (var ingredient in recipe.ingredients) {
                    var def = ingredient.filter.AllowedThingDefs.Where(d => d.category == ThingCategory.Item && d.ingestible != null && d.ingestible.HumanEdible && !d.IsDrug && !d.IsCorpse)
                        .OrderBy(d => d.defName, StringComparer.Ordinal).FirstOrDefault();
                    if (def == null) return Refuse("No edible item satisfies an ingredient of " + recipe.defName + ".");
                    var remaining = (int)Math.Ceiling(ingredient.GetBaseCount() * 40);
                    var total = remaining;
                    while (remaining > 0) {
                        if (next >= free.Count) return Refuse("No room left in the kitchen for the ingredients of " + recipe.defName + ".");
                        var stack = ThingMaker.MakeThing(def);
                        stack.stackCount = Math.Min(def.stackLimit, remaining); remaining -= stack.stackCount;
                        GenSpawn.Spawn(stack, free[next++], map);
                        stack.SetForbidden(false, false);
                    }
                    seeded.Add(new { def = def.defName, count = total });
                }
                foreach (var p in colonists.Where(p => !p.WorkTypeIsDisabled(Cooking))) {
                    p.workSettings.EnableAndInitialize();
                    p.workSettings.SetPriority(Cooking, 1);
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2);
                }

                // The baby, born daysToChild days short of the first Child stage.
                var baby = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, player, forceGenerateNewPawn: true,
                    canGeneratePawnRelations: false, allowAddictions: false, developmentalStages: DevelopmentalStage.Baby));
                if (!baby.DevelopmentalStage.Baby()) return Refuse("The generated pawn is not a baby.");
                var ages = baby.RaceProps.lifeStageAges;
                var childStage = ages.FirstOrDefault(a => a.def.developmentalStage == DevelopmentalStage.Child);
                if (childStage == null) return Refuse("The race has no Child life stage.");
                var ticks = (long)(childStage.minAge * GenDate.TicksPerYear) - (long)(daysToChild * GenDate.TicksPerDay);
                baby.ageTracker.AgeBiologicalTicks = ticks;
                baby.ageTracker.AgeChronologicalTicks = ticks;
                if (!baby.DevelopmentalStage.Baby() || baby.DevelopmentalStage.Newborn()) return Refuse("The baby is not a baby after its age was set.");
                GenSpawn.Spawn(baby, inside.CenterCell, map);
                baby.needs.food.CurLevelPercentage = 0.45f;
                if (ChildcareUtility.CanBreastfeedPlayerPawns.Any()) return Refuse("A colonist can breastfeed after staging.");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    babyId = baby.GetUniqueLoadID(), benchId = stove.GetUniqueLoadID(), recipe = recipe.defName,
                    // Every baby-edible recipe of the stove: the controller picks one (its bulk recipe over the single-item one).
                    recipes = stove.def.AllRecipes.Where(r => r.products.Count == 1 && BabyEdible(r.products[0].thingDef)).Select(r => r.defName).ToList(),
                    product = recipe.products[0].thingDef.defName, ingredients = seeded,
                    childMinAgeYears = childStage.minAge, babyAgeTicks = ticks, daysToChild,
                    foodLevel = baby.needs.food.CurLevelPercentage, colonists = colonists.Count,
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/baby_care_inspect", Description = "Read-only baby-care postcondition for the first-child fixture: the baby's developmental stage, age and food level, malnutrition, and the baby food bills and stock.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string babyId, string benchId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !ModsConfig.BiotechActive) return Refuse("A Biotech map is required.");
                var baby = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == babyId);
                var bench = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().FirstOrDefault(b => b.GetUniqueLoadID() == benchId);
                if (baby == null) return Refuse("The baby is not on the map.");
                var bills = bench == null ? new List<object>() : bench.BillStack.Bills.OfType<Bill_Production>()
                    .Select(b => (object)new { recipe = b.recipe.defName, repeatMode = b.repeatMode.defName, suspended = b.suspended,
                        babyEdible = b.recipe.products.Any(p => BabyEdible(p.thingDef)) }).ToList();
                var stock = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).Where(t => t.Spawned && BabyEdible(t.def))
                    .Sum(t => t.stackCount * t.GetStatValue(StatDefOf.Nutrition));
                var malnutrition = baby.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                return new {
                    success = true, tick = Find.TickManager.TicksGame, dead = baby.Dead, stage = baby.DevelopmentalStage.ToString(),
                    lifeStage = baby.ageTracker.CurLifeStage.defName, ageTicks = baby.ageTracker.AgeBiologicalTicks,
                    foodLevel = baby.needs.food.CurLevelPercentage, malnutrition = malnutrition?.Severity ?? 0f,
                    bills, babyEdibleNutrition = (float)stock, breastfeeders = ChildcareUtility.CanBreastfeedPlayerPawns.Count(),
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
