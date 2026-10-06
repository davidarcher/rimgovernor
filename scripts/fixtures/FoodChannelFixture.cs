using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using HarmonyLib;

namespace HomeBridge.BridgeTools
{
    // Disposable initial state. Cases add their channel after this
    // reset; nothing here advances time or suppresses ordinary simulation.
    public sealed class FoodChannelFixture
    {
        [Tool("test/food_channels_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Strip food stocks (including held food), crops, growing zones, animals, corpses and food-producing buildings from a paused disposable map. Seed exactly stockUnits of foodDef. Channel cases add only the source they prove afterwards.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken,
            int stockUnits = 0, string foodDef = "MealSurvivalPack")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var timer = Stopwatch.StartNew();
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("Paused disposable map required.");
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

        private static float reserveEaten;
        private static bool reservePatched;
        private static void ReserveIngested(Thing __instance, Pawn ingester, float __result)
        {
            if (__instance.def.defName == "Pemmican" && ingester?.IsColonist == true) reserveEaten += __result;
        }

        [Tool("test/food_reserve_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Add a roofed walled food room with a fueled stove, a food stockpile, pemmican research, unforbidden pemmican (seedShare of a reserveDays target) and meat, and a 10000 raw rice runway and 1000 wood outside it to the empty-channel fixture; every colonist cooks. Nothing is forbidden and no bill is preinstalled.")]
        public async Task<object> ReservePrepare(IRimBridgeContext ctx, CancellationToken cancellationToken, double reserveDays = 5, double seedShare = 0.9)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required");
                if (!(reserveDays > 0 && reserveDays <= 60) || !(seedShare >= 0 && seedShare < 1)) throw new ArgumentException("reserveDays must be in (0,60] and seedShare in [0,1)");
                var people = map.mapPawns.FreeColonistsSpawned.ToList();
                // Seed a share of the reserve target (1.6 nutrition per colonist-day,
                // 0.05 per pemmican) so the stock stays short of it and the bill refills the rest.
                int pemmican = (int)Math.Floor(reserveDays * people.Count * 1.6 / 0.05 * seedShare);
                var cook = people.First(p => !p.Downed);
                // A rerun on a world that already holds the food room (a resumed
                // checkpoint) reuses it and its stock instead of clearing ground again.
                var room = CellRect.Empty;
                Building_WorkTable stove = null;
                var existing = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Where(z => z.cells.Count > 0 && z.settings.Priority == StoragePriority.Important).Select(z => CellRect.FromLimits(z.cells.Min(c => c.x), z.cells.Min(c => c.z), z.cells.Max(c => c.x), z.cells.Max(c => c.z))).ToList();
                foreach (var rect in existing) { stove = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().FirstOrDefault(b => b.def.defName == "FueledStove" && rect.Contains(b.Position)); if (stove != null) { room = rect.ExpandedBy(1); break; } }
                foreach (var p in people) { p.jobs.StopAll(); p.workSettings.EnableAndInitialize(); p.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking"), 1); if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) p.workSettings.SetPriority(WorkTypeDefOf.Hauling, 2); p.skills.GetSkill(SkillDefOf.Cooking).Level = 8; }
                if (stove != null) pemmican = map.listerThings.ThingsOfDef(ThingDef.Named("Pemmican")).Sum(t => t.stackCount);
                else {
                foreach (var c in GenRadial.RadialCellsAround(cook.Position, 25, true)) {
                    var rect = CellRect.CenteredOn(c, 11, 9);
                    if (rect.Cells.All(x => x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetEdifice(map) == null && x.GetTerrain(map).passability != Traversability.Impassable && !x.GetThingList(map).Any(t => t is Pawn))) { room = rect; break; }
                }
                if (room.IsEmpty) throw new InvalidOperationException("No clear ground for the food room");
                Thing Spawn(string name, IntVec3 cell) { var def = ThingDef.Named(name); var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null); if (def.CanHaveFaction) thing.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(thing, cell, map); thing.SetForbidden(false, false); return thing; }
                var door = new IntVec3(room.minX, 0, room.CenterCell.z);
                foreach (var cell in room.Cells) {
                    foreach (var thing in cell.GetThingList(map).ToList()) if (!(thing is Pawn)) thing.Destroy(DestroyMode.Vanish);
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                    if (cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ) Spawn(cell == door ? "Door" : "Wall", cell);
                }
                var inside = room.ContractedBy(1);
                stove = (Building_WorkTable)Spawn("FueledStove", new IntVec3(inside.maxX - 1, 0, inside.CenterCell.z));
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
                }
                reserveEaten = 0;
                if (!reservePatched) { new Harmony("rimgovernor.fixture.foodreserve").Patch(AccessTools.Method(typeof(Thing), "Ingested"), postfix: new HarmonyMethod(typeof(FoodChannelFixture), nameof(ReserveIngested))); reservePatched = true; }
                return new { success = true, bench = stove.GetUniqueLoadID(), pemmican, colonists = people.Count, room = new { x = room.minX, z = room.minZ, w = room.Width, h = room.Height } };
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
                    pemmican = things.Where(t => t.def.defName == "Pemmican").Select(t => new { id = t.GetUniqueLoadID(), units = t.stackCount, forbidden = t.Spawned && t.IsForbidden(Faction.OfPlayer), roofed = t.Spawned && t.Position.Roofed(map), stored = t.Spawned && t.IsInAnyStorage(), x = t.Position.x, z = t.Position.z }).ToList(),
                    otherFood = things.Where(t => !(t is Pawn) && t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible && t.def.defName != "Pemmican").Sum(t => t.stackCount),
                    hungriest = map.mapPawns.FreeColonistsSpawned.Min(p => p.needs.food.CurLevelPercentage),
                    bills = map.listerThings.AllThings.OfType<Building_WorkTable>().SelectMany(b => b.BillStack.Bills).OfType<Bill_Production>().Select(b => new { id = b.GetUniqueLoadID(), recipe = b.recipe.defName, target = b.targetCount, repeat = b.repeatMode.defName, suspended = b.suspended, paused = b.paused, shouldDo = b.ShouldDoNow(), count = b.recipe.WorkerCounter.CountProducts(b), fuel = (b.billStack.billGiver as Thing)?.TryGetComp<CompRefuelable>()?.Fuel ?? -1f }).ToList(),
                    jobs = map.mapPawns.FreeColonistsSpawned.Select(p => new { name = p.LabelShort, job = p.CurJob?.def.defName ?? "", target = p.CurJob?.targetA.Thing?.def.defName ?? "", bill = p.CurJob?.bill?.recipe.defName ?? "", cooking = p.workSettings.GetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking")), hauling = p.workSettings.GetPriority(WorkTypeDefOf.Hauling) }).ToList() };
            }, cancellationToken);
        }

        [Tool("test/food_starving_prepare", Description = "UNSAFE FOR MODEL EXECUTION. On the empty-channel fixture, make the colony a starving tribal one for the food/starving-tribal diagnosis (#2141): destroy every colonist's weapons (worn and carried), delete every butcher bill, enable Hunting for every colonist who can, drop colonists to a hungry food level, and spawn animalCount wild animalKind at fixed offsets from the colonists' centre.")]
        public async Task<object> StarvingPrepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string animalKind = "Deer", int animalCount = 6, double foodLevel = 0.3)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(animalKind) ?? throw new ArgumentException("No PawnKindDef " + animalKind);
                if (animalCount < 0 || animalCount > 20) throw new ArgumentException("animalCount must be 0..20.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count == 0) throw new InvalidOperationException("No colonists.");
                var destroyed = new List<string>();
                foreach (var p in people)
                {
                    p.jobs?.StopAll();
                    foreach (var weapon in p.equipment.AllEquipmentListForReading.ToList()) { destroyed.Add(weapon.def.defName); weapon.Destroy(DestroyMode.Vanish); }
                    foreach (var weapon in p.inventory.innerContainer.Where(t => t.def.IsWeapon).ToList()) { destroyed.Add(weapon.def.defName); weapon.Destroy(DestroyMode.Vanish); }
                    if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting)) p.workSettings.SetPriority(WorkTypeDefOf.Hunting, 3);
                    if (p.needs?.food != null) p.needs.food.CurLevelPercentage = (float)foodLevel;
                }
                var bills = 0;
                foreach (var giver in map.listerThings.AllThings.OfType<IBillGiver>().ToList())
                    foreach (var bill in giver.BillStack.Bills.OfType<Bill_Production>().Where(b => NativeRecipeRoles.ButcherFlesh(b.recipe)).ToList())
                    { giver.BillStack.Delete(bill); bills++; }
                var anchor = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                // Fixed offsets (radius 30 to 45 round the centre, 60 degrees
                // apart) so a rerun on the same save stages the same herd; the
                // nearest standable, unfogged cell stands in for a blocked one.
                var spawned = new List<object>();
                for (var i = 0; i < animalCount; i++)
                {
                    var angle = i * Math.PI / 3;
                    var radius = 30 + 5 * (i % 4);
                    var want = new IntVec3(anchor.x + (int)Math.Round(radius * Math.Cos(angle)), 0, anchor.z + (int)Math.Round(radius * Math.Sin(angle)));
                    var cell = GenRadial.RadialCellsAround(want, 8, true).FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetEdifice(map) == null);
                    if (!cell.IsValid || !cell.InBounds(map)) throw new InvalidOperationException("No open cell near " + want);
                    var animal = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, null, forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowAddictions: false));
                    GenSpawn.Spawn(animal, cell, map);
                    spawned.Add(new { id = animal.GetUniqueLoadID(), x = cell.x, z = cell.z, distance = Math.Round(cell.DistanceTo(anchor), 1) });
                }
                return new { success = true, colonists = people.Count, weaponsDestroyed = destroyed, butcherBillsDeleted = bills, animals = spawned, anchor = new { x = anchor.x, z = anchor.z } };
            }, cancellationToken);
        }

        [Tool("test/food_starving_observe", Description = "Read what gates a hunt on the starving-tribal fixture (#2141); no mutations. Per colonist: weapon, Hunting and Cooking work priorities, food level; per wild animal: position and NativeHuntAcquisition.Ineligible; the butcher bills, pending hunt designations, wildlife counts and corpses.")]
        public async Task<object> StarvingObserve(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("Loaded map required.");
                var cooking = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
                var colonists = map.mapPawns.FreeColonistsSpawned.Select(p => new {
                    id = p.GetUniqueLoadID(), name = p.LabelShort, downed = p.Downed,
                    weapon = p.equipment?.Primary?.def.defName ?? "", ranged = p.equipment?.Primary?.def.IsRangedWeapon == true,
                    hunting = p.workSettings?.GetPriority(WorkTypeDefOf.Hunting) ?? -1, huntingDisabled = p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting),
                    cooking = cooking == null || p.workSettings == null ? -1 : p.workSettings.GetPriority(cooking),
                    food = p.needs?.food?.CurLevelPercentage ?? -1f, job = p.CurJob?.def.defName ?? "" }).ToList();
                var wild = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == null && p.RaceProps.Animal && !p.Dead).ToList();
                var animals = wild.Select(p => new { id = p.GetUniqueLoadID(), race = p.def.defName, x = p.Position.x, z = p.Position.z,
                    designated = NativeHuntAcquisition.Designated(p), ineligible = NativeHuntAcquisition.Ineligible(p) ?? "eligible" }).ToList();
                var bills = map.listerThings.AllThings.OfType<IBillGiver>().SelectMany(g => g.BillStack.Bills.OfType<Bill_Production>()
                    .Select(b => new { bench = (g as Thing)?.def.defName ?? "", recipe = b.recipe.defName, butcher = NativeRecipeRoles.ButcherFlesh(b.recipe), suspended = b.suspended, paused = b.paused })).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, colonists, animals, bills,
                    pendingHunts = animals.Count(a => a.designated), wildAnimals = animals.Count,
                    corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Count,
                    foodItems = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.def.IsNutritionGivingIngestible).Sum(t => t.stackCount) };
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
