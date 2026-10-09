using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Test-only pinned stock/participant setup, inventory readback and ordinary jobs.
    public sealed class TradeFixture
    {
        [Tool("test/trade_fixture", Description = "Disposable trade acceptance setup/readback; excluded from production builds and model execution.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "snapshot", string traderId = null, string pawnId = null, int silver = 600, int medicine = 30, string foodMode = "", int steel = 0,
            string channel = "", string caravanId = null)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || (!Find.TickManager.Paused && action != "session_inventory" && action != "roundtrip_state")) throw new InvalidOperationException("Pause a disposable colony first.");
                if (action == "roundtrip_setup") return PrepareRoundtrip(map);
                if (action == "roundtrip_state") return RoundtripState(map, traderId);
                if (action == "session_setup") return PrepareSession(map, channel);
                if (action.StartsWith("request_")) return RequestFixture(map, action, channel, pawnId, traderId);
                if (action == "session_inventory") return SessionInventory(map, channel, traderId, caravanId);
                Func<bool, string, object> incident = (caravan, caravanKind) => {
                    var def = DefDatabase<IncidentDef>.GetNamed(caravan ? "TraderCaravanArrival" : "VisitorGroup");
                    var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                    var kind = DefDatabase<TraderKindDef>.GetNamed(caravan ? caravanKind : "Visitor_Neolithic_Standard");
                    parms.faction = Find.FactionManager.AllFactions.First(f => !f.IsPlayer && !f.defeated
                        && !f.HostileTo(Faction.OfPlayer) && (caravan
                            ? f.def.caravanTraderKinds.Contains(kind) : f.def.visitorTraderKinds.Contains(kind)));
                    if (caravan) parms.traderKind = kind;
                    var eligible = def.Worker.CanFireNow(parms);
                    var applied = eligible && def.Worker.TryExecute(parms);
                    var traderIds = applied
                        ? map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == parms.faction && p.trader != null && p.trader.traderKind != null)
                            .Select(p => p.GetUniqueLoadID()).ToArray()
                        : new string[0];
                    return new { eligible, applied, definition = def.defName, traderKind = kind.defName,
                        faction = parms.faction.Name, traderIds };
                };
                if (action == "incident" || action == "visitor_incident") return incident(action == "incident", "Caravan_Outlander_BulkGoods");
                // The routine trade case: a colony with no medicine
                // and unforbidden silver inside the home area (a caravan
                // buys only home-area or stored items), then an arriving
                // neolithic bulk-goods caravan (the kind that trades herbal
                // medicine) whose pack animals carry it (a caravan's goods
                // are its carriers' inventories). The
                // caravan walks in from the map edge on its own; nothing is
                // teleported, and the negotiator is native's to walk.
                if (action == "routine_setup")
                {
                    if (foodMode != "" && foodMode != "bridge" && foodMode != "surplus") throw new ArgumentException("Unknown food mode.");
                    foreach (var stack in map.listerThings.AllThings.Where(t => t.def.IsMedicine).ToList()) stack.Destroy();
                    foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                        foreach (var held in colonist.inventory.innerContainer.Where(t => t.def.IsMedicine).ToList()) held.Destroy();
                    var anchor = map.mapPawns.FreeColonistsSpawned.First().Position;
                    var silverCell = GenRadial.RadialCellsAround(anchor, 8, true).First(c => c.InBounds(map) && c.Standable(map)
                        && !c.Fogged(map) && c.GetFirstItem(map) == null && c.GetEdifice(map) == null);
                    // Silver stacks cap at 500; a larger request spawns
                    // several stacks around the same cell.
                    var silverStacks = new System.Collections.Generic.List<Thing>();
                    for (var remaining = silver; remaining > 0; remaining -= ThingDefOf.Silver.stackLimit)
                    {
                        var silverStack = ThingMaker.MakeThing(ThingDefOf.Silver);
                        silverStack.stackCount = Math.Min(remaining, ThingDefOf.Silver.stackLimit);
                        GenPlace.TryPlaceThing(silverStack, silverCell, map, ThingPlaceMode.Near);
                        silverStack.SetForbidden(false, false);
                        map.areaManager.Home[silverStack.Position] = true;
                        silverStacks.Add(silverStack);
                    }
                    var silverStack0 = silverStacks[0];
                    if (steel > 0)
                    {
                        foreach (var old in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList()) old.Destroy();
                        foreach (var colonist in map.mapPawns.FreeColonistsSpawned)
                        {
                            foreach (var held in colonist.inventory.innerContainer.Where(t => t.def == ThingDefOf.Steel).ToList()) held.Destroy();
                            colonist.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Construction"), 0);
                        }
                        // GenSpawn clamps an oversized stack to the stack limit (75).
                        for (var remaining = steel; remaining > 0; remaining -= ThingDefOf.Steel.stackLimit)
                        {
                            var hoard = ThingMaker.MakeThing(ThingDefOf.Steel);
                            hoard.stackCount = Math.Min(remaining, ThingDefOf.Steel.stackLimit);
                            if (!GenPlace.TryPlaceThing(hoard, silverCell, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Steel placement failed.");
                            hoard.SetForbidden(false, false);
                            map.areaManager.Home[hoard.Position] = true;
                        }
                    }
                    var foodUnits = 0;
                    var dailyNutrition = map.mapPawns.FreeColonistsSpawned.Sum(p => (double)p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000);
                    if (foodMode != "")
                    {
                        // FoodChannelFixture has already removed every competing
                        // channel. Seed a one-day emergency or a measured rice surplus.
                        var foodDef = DefDatabase<ThingDef>.GetNamed(foodMode == "bridge" ? "MealSimple" : "RawRice");
                        foodUnits = foodMode == "bridge" ? (int)Math.Ceiling(dailyNutrition / foodDef.GetStatValueAbstract(StatDefOf.Nutrition)) : 6000;
                        for (var left = foodUnits; left > 0; left -= foodDef.stackLimit)
                        {
                            var food = ThingMaker.MakeThing(foodDef); food.stackCount = Math.Min(left, foodDef.stackLimit);
                            if (!GenPlace.TryPlaceThing(food, silverCell, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Food placement failed.");
                            food.SetForbidden(false, false); map.areaManager.Home[food.Position] = true;
                        }
                        if (foodMode == "surplus")
                        {
                            var stoveDef = DefDatabase<ThingDef>.GetNamed("FueledStove");
                            var stoveCell = GenRadial.RadialCellsAround(anchor, 15, true).First(c => GenAdj.OccupiedRect(c, Rot4.North, stoveDef.size).Cells.All(x => x.InBounds(map) && x.Standable(map) && !x.Fogged(map) && x.GetEdifice(map) == null && x.GetFirstItem(map) == null));
                            var stove = (Building_WorkTable)GenSpawn.Spawn(ThingMaker.MakeThing(stoveDef), stoveCell, map);
                            stove.SetFaction(Faction.OfPlayer); stove.TryGetComp<CompRefuelable>().Refuel(50);
                            var bill = (Bill_Production)DefDatabase<RecipeDef>.GetNamed("CookMealFine").MakeNewBill();
                            bill.repeatMode = BillRepeatModeDefOf.TargetCount; bill.targetCount = 30; stove.BillStack.AddBill(bill);
                            // Preserve inventory for the trade assertion; the active
                            // fine bill supplies intent, this case does not cook it.
                            foreach (var cook in map.mapPawns.FreeColonistsSpawned) cook.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Cooking"), 0);
                        }
                    }
                    // Outlander bulk traders buy steel; neolithic traders cover the herbal-medicine and food cases.
                    var arrival = incident(true, steel > 0 ? "Caravan_Outlander_BulkGoods" : "Caravan_Neolithic_BulkGoods");
                    var traderPawns = map.mapPawns.AllPawnsSpawned.Where(p => p.trader != null && p.trader.traderKind != null
                        && p.Faction != null && !p.Faction.IsPlayer).ToList();
                    var stocked = new System.Collections.Generic.List<object>();
                    foreach (var traderPawn in traderPawns)
                    {
                        if (steel > 0)
                        {
                            if (!traderPawn.trader.traderKind.WillTrade(ThingDefOf.Steel)) throw new InvalidOperationException("Trader will not buy steel.");
                            var steelCarrier = traderPawn.GetLord()?.ownedPawns.FirstOrDefault(p => p.GetTraderCaravanRole() == TraderCaravanRole.Carrier) ?? traderPawn;
                            var payment = ThingMaker.MakeThing(ThingDefOf.Silver);
                            payment.stackCount = steel * 10;
                            if (!steelCarrier.inventory.innerContainer.TryAdd(payment)) throw new InvalidOperationException("Trader silver could not be stocked.");
                            foreach (var caravanPawn in traderPawn.GetLord().ownedPawns)
                            {
                                caravanPawn.Position = CellFinder.StandableCellNear(anchor, map, 6); caravanPawn.Notify_Teleported();
                            }
                        }
                        if (foodMode != "")
                        {
                            foreach (var old in traderPawn.trader.Goods.Where(t => t.def.IsNutritionGivingIngestible || t.def.IsMedicine || t.def.defName == "ComponentIndustrial").ToList()) old.Destroy();
                            var food = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(foodMode == "bridge" ? "Pemmican" : "Meat_Muffalo"));
                            food.stackCount = foodMode == "bridge" ? 2000 : 300;
                            var foodCarrier = traderPawn.GetLord()?.ownedPawns.FirstOrDefault(p => p.GetTraderCaravanRole() == TraderCaravanRole.Carrier) ?? traderPawn;
                            if (!foodCarrier.inventory.innerContainer.TryAdd(food)) throw new InvalidOperationException("Trader food could not be stocked.");
                            var listedFood = traderPawn.trader.Goods.Any(t => t.def == food.def && traderPawn.trader.traderKind.WillTrade(t.def));
                            if (!listedFood) throw new InvalidOperationException("Trader does not list the requested food.");
                            stocked.Add(new { traderId = traderPawn.GetUniqueLoadID(), listed = true, count = food.stackCount });
                            // Admit without a long caravan walk. Native trading,
                            // negotiation and settlement remain ordinary operations.
                            foreach (var caravanPawn in traderPawn.GetLord().ownedPawns)
                            {
                                caravanPawn.Position = CellFinder.StandableCellNear(anchor, map, 6); caravanPawn.Notify_Teleported();
                            }
                            continue;
                        }
                        if (traderPawn.trader.Goods.Any(t => t.def.IsMedicine)) continue;
                        var stack = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                        stack.stackCount = medicine;
                        var lord = traderPawn.GetLord();
                        var carrier = lord?.ownedPawns.FirstOrDefault(p => p.GetTraderCaravanRole() == TraderCaravanRole.Carrier) ?? traderPawn;
                        var added = carrier.inventory.innerContainer.TryAdd(stack);
                        var listed = traderPawn.trader.Goods.Any(t => t.def.IsMedicine && traderPawn.trader.traderKind.WillTrade(t.def));
                        stocked.Add(new { traderId = traderPawn.GetUniqueLoadID(), carrierId = carrier.GetUniqueLoadID(), added, listed, count = medicine });
                    }
                    return new { success = traderPawns.Count > 0, arrival, silver = silverStacks.Sum(t => t.stackCount), silverId = silverStack0.ThingID,
                        x = silverCell.x, z = silverCell.z, stocked, colonists = map.mapPawns.FreeColonistsSpawnedCount,
                        colonyMedicine = map.listerThings.AllThings.Count(t => t.def.IsMedicine), foodMode, foodUnits, dailyNutrition, steel };
                }
                if (action == "teleport_adjacent")
                {
                    var traderPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == traderId);
                    var negotiatorPawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
                    if (traderPawn == null || negotiatorPawn == null) throw new InvalidOperationException("Exact trader and negotiator are required.");
                    var cell = GenAdjFast.AdjacentCells8Way(traderPawn.Position)
                        .FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.Walkable(map));
                    if (!cell.IsValid) throw new InvalidOperationException("No standable cell adjacent to the trader.");
                    negotiatorPawn.Position = cell;
                    negotiatorPawn.Notify_Teleported();
                    return new { success = true, x = cell.x, y = cell.y, z = cell.z };
                }
                // Read back the trader's and negotiator's raw eligibility fields
                // for acceptance diagnostics.
                if (action == "state")
                {
                    var traderPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == traderId);
                    var negotiatorPawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
                    if (traderPawn == null || negotiatorPawn == null) throw new InvalidOperationException("Exact trader and negotiator are required.");
                    return new {
                        trader = new {
                            x = traderPawn.Position.x, y = traderPawn.Position.y, z = traderPawn.Position.z,
                            canTradeNow = traderPawn.CanTradeNow,
                            dismissed = traderPawn.mindState != null && traderPawn.mindState.traderDismissed,
                        },
                        negotiator = new {
                            x = negotiatorPawn.Position.x, y = negotiatorPawn.Position.y, z = negotiatorPawn.Position.z,
                            downed = negotiatorPawn.Downed, dead = negotiatorPawn.Dead,
                            mental = negotiatorPawn.InMentalState,
                            socialDisabled = negotiatorPawn.WorkTagIsDisabled(WorkTags.Social),
                        },
                    };
                }
                var trader = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == traderId);
                var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == pawnId);
                if (action == "clear_trade_home")
                {
                    if (trader == null || pawn == null) throw new InvalidOperationException("Exact participants required.");
                    var cells = ((ITrader)trader).ColonyThingsWillingToBuy(pawn)
                        .Where(t => t.Spawned && t.def.category == ThingCategory.Item).Select(t => t.Position).Distinct().ToArray();
                    var designator = new Designator_AreaHomeClear();
                    var cleared = 0;
                    foreach (var cell in cells)
                        if (designator.CanDesignateCell(cell).Accepted)
                        {
                            designator.DesignateSingleCell(cell);
                            cleared++;
                        }
                    return new { cleared, silverStillEligible = ((ITrader)trader).ColonyThingsWillingToBuy(pawn)
                        .Any(t => t.def == ThingDefOf.Silver && t.stackCount > 0) };
                }
                if (action == "trade_job" || action == "dismiss_job")
                {
                    if (trader == null || pawn == null || !trader.CanTradeNow || trader.mindState.traderDismissed
                        || pawn.skills.GetSkill(SkillDefOf.Social).TotallyDisabled
                        || !pawn.CanReach(trader, PathEndMode.OnCell, Danger.Deadly)
                        || !pawn.CanTradeWith(trader.Faction, trader.TraderKind).Accepted)
                        throw new InvalidOperationException("Ordinary trade input is unavailable.");
                    var definition = action == "trade_job" ? JobDefOf.TradeWithPawn : JobDefOf.DismissTrader;
                    var job = JobMaker.MakeJob(definition, trader);
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    // An adjacent dismissal can complete and return its Job to the pool immediately.
                    return new { ordered = pawn.CurJobDef == definition || trader.mindState.traderDismissed,
                        job = definition.defName };
                }
                if (action != "snapshot") throw new ArgumentException("Unknown fixture action.");
                Func<Thing, object> describe = t => new { id = t.ThingID, defName = t.def.defName,
                    count = t.stackCount, spawned = t.Spawned, forbidden = t.Spawned && t.IsForbidden(Faction.OfPlayer),
                    traderProtected = trader?.GetLord()?.extraForbiddenThings.Contains(t) ?? false,
                    x = t.Position.x, z = t.Position.z };
                return new { tick = Find.TickManager.TicksGame,
                    traderPresent = trader != null, traderDismissed = trader?.mindState.traderDismissed,
                    dialogOpen = Find.WindowStack.WindowOfType<Dialog_Trade>() != null,
                    negotiator = TradeSession.Active ? TradeSession.playerNegotiator.GetUniqueLoadID() : null,
                    ground = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item).Select(describe).ToArray(),
                    goods = trader?.trader?.Goods.Where(t => t.def.category == ThingCategory.Item).Select(describe).ToArray(),
                    colonists = map.mapPawns.FreeColonistsSpawned.Select(p => new { id = p.GetUniqueLoadID(), name = p.LabelShort,
                        social = p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled ? -1 : p.skills.GetSkill(SkillDefOf.Social).Level,
                        job = p.CurJobDef?.defName, x = p.Position.x, z = p.Position.z }).ToArray() };
            }, cancellationToken);
        }

        // Only a precondition: no caravan, Project, order, purchase or home route.
        private static object PrepareRoundtrip(Map map)
        {
            var people = map.mapPawns.FreeColonistsSpawned.ToList();
            foreach (var p in people)
            {
                p.jobs.StopAll();
                foreach (var old in p.inventory.innerContainer.ToList()) old.Destroy();
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefs.Where(w => !p.WorkTypeIsDisabled(w)))
                    p.workSettings.SetPriority(work, 1);
                if (p.needs.food != null) p.needs.food.CurLevelPercentage = 1f;
                if (p.needs.rest != null) p.needs.rest.CurLevelPercentage = 1f;
            }
            foreach (var old in map.listerThings.AllThings.Where(t => t.def.IsNutritionGivingIngestible || t.def == ThingDefOf.Silver).ToList()) old.Destroy();
            var cell = map.Center + new IntVec3(3, 0, 3);
            void Place(ThingDef def, int count)
            {
                for (var left = count; left > 0; left -= def.stackLimit)
                {
                    var t = ThingMaker.MakeThing(def); t.stackCount = Math.Min(left, def.stackLimit);
                    if (!GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near)) throw new InvalidOperationException("Roundtrip stock placement failed.");
                    t.SetForbidden(false, false); map.areaManager.Home[t.Position] = true;
                }
            }
            // Retain the three-day home floor plus the short trip's pack,
            // while staying below seven days even after one consumer leaves.
            Place(ThingDef.Named("MealSurvivalPack"), 40);
            Place(ThingDefOf.Silver, 4000);
            var faction = Find.FactionManager.AllFactions.First(f => f.def.defName == "OutlanderCivil" && !f.defeated && !f.HostileTo(Faction.OfPlayer));
            var neighbors = new System.Collections.Generic.List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
            var tile = neighbors.OrderBy(t => t.tileId).First(t => !Find.World.Impassable(t) && !Find.WorldObjects.AnyMapParentAt(t));
            var seller = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            seller.Tile = tile; seller.SetFaction(faction); seller.Name = "AutonomousRoundtripFixture";
            Find.WorldObjects.Add(seller);
            var generated = seller.Goods.ToList();
            var holder = seller.trader.GetDirectlyHeldThings();
            foreach (var old in generated) { holder.Remove(old); if (!(old is Pawn)) old.Destroy(); }
            // Stock every vanilla prepared-food definition this seller actually
            // trades; the normal policy still chooses its own demand definition.
            var meals = DefDatabase<ThingDef>.AllDefs.Where(d => d.ingestible != null && (d.ingestible.foodType & FoodTypeFlags.Meal) != 0
                && d.defName != "MealSurvivalPack" && seller.TraderKind.WillTrade(d)).OrderBy(d => d.defName).ToList();
            foreach (var def in meals) { var t = ThingMaker.MakeThing(def); t.stackCount = 500; holder.TryAdd(t); }
            var money = ThingMaker.MakeThing(ThingDefOf.Silver); money.stackCount = 10000; holder.TryAdd(money);
            if (!seller.CanTradeNow || meals.Count == 0 || people.Count < 6) throw new InvalidOperationException("Roundtrip requires six home crew and a stocked legal seller.");
            return new { success = true, traderId = seller.GetUniqueLoadID(), homeTile = map.Tile.tileId, settlementTile = tile.tileId,
                crew = people.Select(p => p.GetUniqueLoadID()).ToArray(), meals = meals.Select(d => d.defName).ToArray(),
                sellerMeals = meals.Count * 500, homeMeals = 0, packingMeals = 40, silver = 4000 };
        }

        private static object RoundtripState(Map map, string traderId)
        {
            var seller = Find.WorldObjects.Settlements.Single(s => s.GetUniqueLoadID() == traderId);
            bool BoughtFood(Thing t) => t.def.ingestible != null && (t.def.ingestible.foodType & FoodTypeFlags.Meal) != 0 && t.def.defName != "MealSurvivalPack";
            int Count(System.Collections.Generic.IEnumerable<Thing> things) => things.Where(t => !t.Destroyed && BoughtFood(t)).Sum(t => t.stackCount);
            var caravans = Find.WorldObjects.Caravans.Where(c => c.Faction == Faction.OfPlayer).ToList();
            return new { tick = Find.TickManager.TicksGame, sellerMeals = Count(seller.Goods), sellerSilver = seller.Goods.Where(t => t.def == ThingDefOf.Silver).Sum(t => t.stackCount),
                homeMeals = Count(map.listerThings.AllThings.Where(t => t.Spawned).Concat(map.mapPawns.FreeColonistsSpawned.SelectMany(p => p.inventory.innerContainer))),
                homeCrew = map.mapPawns.FreeColonistsSpawned.Select(p => p.GetUniqueLoadID()).ToArray(),
                caravans = caravans.Select(c => new { id = c.GetUniqueLoadID(), tile = c.Tile.tileId, moving = c.pather.Moving,
                    crew = c.PawnsListForReading.Select(p => p.GetUniqueLoadID()).ToArray(), meals = Count(CaravanInventoryUtility.AllInventoryItems(c)),
                    silver = CaravanInventoryUtility.AllInventoryItems(c).Where(t => t.def == ThingDefOf.Silver).Sum(t => t.stackCount) }).ToArray() };
        }

        // Stock and participants only: opening, pricing and transfer are always
        // production trade operations. Coordinates are pinned to the blank lab.
        private static object PrepareSession(Map map, string channel)
        {
            if (channel != "settlement" && channel != "orbital") throw new ArgumentException("Unknown session channel.");
            var negotiator = map.mapPawns.FreeColonistsSpawned.First(p => !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled);
            var component = DefDatabase<ThingDef>.GetNamed("ComponentIndustrial");
            Thing Stack(ThingDef def, int count) { var t = ThingMaker.MakeThing(def); t.stackCount = count; return t; }
            var origin = map.Center;
            if (channel == "settlement")
            {
                var faction = Find.FactionManager.AllFactions.First(f => f.def.defName == "OutlanderCivil" && !f.defeated && !f.HostileTo(Faction.OfPlayer));
                var neighbors = new System.Collections.Generic.List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
                var tile = neighbors.OrderBy(t => t.tileId).First(t => !Find.World.Impassable(t) && !Find.WorldObjects.AnyMapParentAt(t));
                var settlement = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                settlement.Tile = tile; settlement.SetFaction(faction); settlement.Name = "TradeSessionFixture";
                Find.WorldObjects.Add(settlement);
                // Materialize the vanilla holder, then replace random stock.
                var generated = settlement.Goods.ToList();
                var holder = settlement.trader.GetDirectlyHeldThings();
                foreach (var old in generated) { holder.Remove(old); if (!(old is Pawn)) old.Destroy(); }
                if (!settlement.TraderKind.WillTrade(component) || !settlement.TraderKind.WillTrade(ThingDefOf.MedicineIndustrial))
                    throw new InvalidOperationException("Core settlement does not trade pinned stock.");
                holder.TryAdd(Stack(component, 20)); holder.TryAdd(Stack(ThingDefOf.Silver, 10000));
                negotiator.jobs.EndCurrentJob(JobCondition.InterruptForced); negotiator.DeSpawn();
                foreach (var old in negotiator.inventory.innerContainer.ToList()) old.Destroy();
                var caravan = CaravanMaker.MakeCaravan(new[] { negotiator }, Faction.OfPlayer, tile, true);
                negotiator.inventory.innerContainer.TryAdd(Stack(ThingDefOf.Silver, 2000));
                negotiator.inventory.innerContainer.TryAdd(Stack(ThingDefOf.MedicineIndustrial, 6));
                if (!ReferenceEquals(CaravanVisitUtility.SettlementVisitedNow(caravan), settlement) || !settlement.CanTradeNow)
                    throw new InvalidOperationException("Prepared caravan is not visiting its settlement.");
                return new { success = true, traderId = settlement.GetUniqueLoadID(), caravanId = caravan.GetUniqueLoadID(),
                    pawnId = negotiator.GetUniqueLoadID(), channel, silver = 2000, medicine = 6, components = 0 };
            }
            Thing Spawn(string name, int x, int z)
            {
                var def = DefDatabase<ThingDef>.GetNamed(name);
                var t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                if (def.CanHaveFaction) t.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(t, origin + new IntVec3(x, 0, z), map); return t;
            }
            var generator = Spawn("WoodFiredGenerator", -6, 0);
            generator.TryGetComp<CompRefuelable>().Refuel(50);
            for (var x = -6; x <= 4; x++) Spawn("PowerConduit", x, 0);
            var console = (Building_CommsConsole)Spawn("CommsConsole", 0, 0);
            var beacon = (Building_OrbitalTradeBeacon)Spawn("OrbitalTradeBeacon", 4, 0);
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            foreach (var building in new Thing[] { console, beacon })
            {
                var power = building.TryGetComp<CompPowerTrader>();
                if (power?.PowerNet == null || !power.PowerNet.powerComps.Any(c => c.parent == generator))
                    throw new InvalidOperationException("Fixture comms buildings lack generator connection.");
                power.PowerOn = true;
            }
            foreach (var old in map.listerThings.AllThings.Where(t => t.def == ThingDefOf.Silver || t.def == component || t.def.IsMedicine).ToList()) old.Destroy();
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
                foreach (var old in p.inventory.innerContainer.ToList()) old.Destroy();
            for (var i = 0; i < 4; i++) GenPlace.TryPlaceThing(Stack(ThingDefOf.Silver, 500), origin + new IntVec3(4, 0, 3), map, ThingPlaceMode.Near);
            GenPlace.TryPlaceThing(Stack(ThingDefOf.MedicineIndustrial, 6), origin + new IntVec3(4, 0, 4), map, ThingPlaceMode.Near);
            var kind = DefDatabase<TraderKindDef>.GetNamed("Orbital_BulkGoods");
            var ship = new TradeShip(kind); map.passingShipManager.AddShip(ship);
            ship.GetDirectlyHeldThings().TryAdd(Stack(component, 20));
            ship.GetDirectlyHeldThings().TryAdd(Stack(ThingDefOf.Silver, 10000));
            if (!kind.WillTrade(component) || !kind.WillTrade(ThingDefOf.MedicineIndustrial) || !console.CanUseCommsNow)
                throw new InvalidOperationException("Pinned orbital trade prerequisites unavailable.");
            negotiator.Position = console.InteractionCell; negotiator.Notify_Teleported();
            return new { success = true, traderId = ship.GetUniqueLoadID(), caravanId = "", pawnId = negotiator.GetUniqueLoadID(),
                channel, silver = 2000, medicine = 6, components = 0 };
        }

        private static object RequestFixture(Map map, string action, string channel, string pawnId, string factionId)
        {
            var orbital = channel == "orbital";
            var faction = factionId == null ? Find.FactionManager.AllFactionsVisible.First(f => !f.IsPlayer && !f.defeated
                && (orbital ? f.def.canRequestOrbitalTrader : f.def.canRequestTraders)
                && (orbital ? f.def.orbitalTraderKinds : f.def.caravanTraderKinds).Any(k => k.requestable && k.TitleRequiredToTrade == null)
                && (orbital || f.def.allowedArrivalTemperatureRange.ExpandedBy(-4f).Includes(map.mapTemperature.SeasonalTemp)))
                : Find.FactionManager.AllFactionsVisible.Single(f => f.GetUniqueLoadID() == factionId);
            var pawn = pawnId == null ? map.mapPawns.FreeColonistsSpawned.First(p => !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
                : map.mapPawns.FreeColonistsSpawned.Single(p => p.GetUniqueLoadID() == pawnId);
            var console = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().FirstOrDefault();
            if (action == "request_setup")
            {
                if (orbital && !ModsConfig.OdysseyActive) throw new InvalidOperationException("Odyssey required.");
                Thing Spawn(string name, int x) {
                    var t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(name));
                    if (t.def.CanHaveFaction) t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, map.Center + new IntVec3(x, 0, 0), map); return t;
                }
                var generator = Spawn("WoodFiredGenerator", -6); generator.TryGetComp<CompRefuelable>().Refuel(50);
                for (var x = -6; x <= 0; x++) Spawn("PowerConduit", x);
                console = (Building_CommsConsole)Spawn("CommsConsole", 0);
                map.powerNetManager.UpdatePowerNetsAndConnections_First();
                console.TryGetComp<CompPowerTrader>().PowerOn = true;
                faction.TryAffectGoodwillWith(Faction.OfPlayer, 100 - faction.BaseGoodwillWith(Faction.OfPlayer), false, false);
                faction.lastTraderRequestTick = -240000; faction.lastOrbitalTraderRequestTick = -900000;
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                // Distance gives the interrupted-job control a genuine uncompleted contact.
                pawn.Position = map.Center + new IntVec3(10, 0, 10); pawn.Notify_Teleported();
            }
            else if (action == "request_interrupt") pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            else if (action == "request_low_goodwill") faction.TryAffectGoodwillWith(Faction.OfPlayer, 1 - faction.BaseGoodwillWith(Faction.OfPlayer), false, false);
            else if (action == "request_restore_goodwill") faction.TryAffectGoodwillWith(Faction.OfPlayer, 100 - faction.BaseGoodwillWith(Faction.OfPlayer), false, false);
            else if (action == "request_console_off") console.TryGetComp<CompPowerTrader>().PowerOn = false;
            else if (action == "request_console_on") console.TryGetComp<CompPowerTrader>().PowerOn = true;
            else if (action != "request_state") throw new ArgumentException("Unknown request fixture action.");
            var kind = (orbital ? faction.def.orbitalTraderKinds : faction.def.caravanTraderKinds).First(k => k.requestable && k.TitleRequiredToTrade == null);
            return new { success = true, factionId = faction.GetUniqueLoadID(), pawnId = pawn.GetUniqueLoadID(), consoleId = console.GetUniqueLoadID(),
                traderKind = kind.defName, goodwill = faction.PlayerGoodwill,
                cost = -Faction.OfPlayer.CalculateAdjustedGoodwillChange(faction, orbital ? -30 : -15),
                lastRequestTick = orbital ? faction.lastOrbitalTraderRequestTick : faction.lastTraderRequestTick,
                tick = Find.TickManager.TicksGame, ally = faction.PlayerRelationKind == FactionRelationKind.Ally,
                traders = map.mapPawns.AllPawnsSpawned.Count(p => p.Faction == faction && p.trader?.traderKind == kind),
                ships = map.passingShipManager.passingShips.OfType<TradeShip>().Count(s => s.TraderKind == kind) };
        }

        private static object SessionInventory(Map map, string channel, string traderId, string caravanId)
        {
            ITrader seller;
            System.Collections.Generic.IEnumerable<Thing> inventory;
            if (channel == "settlement")
            {
                var caravan = Find.WorldObjects.Caravans.Single(c => c.GetUniqueLoadID() == caravanId);
                seller = Find.WorldObjects.Settlements.Single(s => s.GetUniqueLoadID() == traderId);
                inventory = CaravanInventoryUtility.AllInventoryItems(caravan);
            }
            else if (channel == "orbital")
            {
                seller = map.passingShipManager.passingShips.OfType<TradeShip>().Single(s => s.GetUniqueLoadID() == traderId);
                // Ground delivery only; an incoming pod is not delivered stock.
                inventory = map.listerThings.AllThings.Where(t => t.Spawned && t.def.category == ThingCategory.Item)
                    .Concat(map.mapPawns.FreeColonistsSpawned.SelectMany(p => p.inventory.innerContainer));
            }
            else throw new ArgumentException("Unknown session channel.");
            int Count(System.Collections.Generic.IEnumerable<Thing> things, string def) => things.Where(t => !t.Destroyed && t.def.defName == def).Sum(t => t.stackCount);
            var owned = inventory.ToList();
            return new { silver = Count(owned, "Silver"), medicine = Count(owned, "MedicineIndustrial"), components = Count(owned, "ComponentIndustrial"),
                sellerComponents = Count(seller.Goods, "ComponentIndustrial"),
                homeComponents = Count(map.listerThings.AllThings.Where(t => t.Spawned), "ComponentIndustrial") };
        }
    }
}
