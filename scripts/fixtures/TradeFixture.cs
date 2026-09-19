using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Test-only incident setup and ordinary ordered jobs. Only routine_setup
    // edits stock (the colony's medicine and silver, the caravan's medicine).
    public sealed class TradeFixture
    {
        [Tool("test/trade_fixture", Description = "Disposable trade acceptance setup/readback; excluded from production builds and model execution.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "snapshot", string traderId = null, string pawnId = null, int silver = 600, int medicine = 30, string foodMode = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Pause a disposable colony first.");
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
                // The routine trade case (#234): a colony with no medicine
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
                    var arrival = incident(true, "Caravan_Neolithic_BulkGoods");
                    var traderPawns = map.mapPawns.AllPawnsSpawned.Where(p => p.trader != null && p.trader.traderKind != null
                        && p.Faction != null && !p.Faction.IsPlayer).ToList();
                    var stocked = new System.Collections.Generic.List<object>();
                    foreach (var traderPawn in traderPawns)
                    {
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
                        colonyMedicine = map.listerThings.AllThings.Count(t => t.def.IsMedicine), foodMode, foodUnits, dailyNutrition };
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
                // Read back the exact raw fields NativeTradeOperations.TraderToken/
                // NegotiatorToken hash, so an acceptance harness can self-compute
                // those tokens client side without guessing at RimWorld's
                // IntVec3.ToString() format or its pawn altitude-layer constant.
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
    }
}
