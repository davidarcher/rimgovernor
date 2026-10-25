using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // Test-only trade participants and pinned stock the stage primitives cannot express, and the inventories the trade
    // read does not cover. Every reply is a RimGovernor.Fixtures.Contracts record.
    public sealed class TradeFixture
    {
        [Tool("test/trade_fixture", Description = "Disposable trade acceptance setup/readback; excluded from production builds and model execution.")]
        public async Task<object> Run(INativeContext ctx, CancellationToken cancellationToken,
            string action = "", string? traderId = null, string? pawnId = null, string channel = "", string? caravanId = null)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || (!Find.TickManager.Paused && action != "session_inventory")) throw new InvalidOperationException("Pause a disposable colony first.");
                if (action == "session_setup") return FixtureKit.Reply(PrepareSession(map, channel));
                if (action.StartsWith("request_")) return FixtureKit.Reply(RequestFixture(map, action, channel, pawnId, traderId));
                if (action == "session_inventory") return FixtureKit.Reply(SessionInventory(map, channel, traderId, caravanId));
                if (action != "teleport_adjacent") throw new ArgumentException("Unknown fixture action.");
                var traderPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == traderId)
                    ?? throw new InvalidOperationException("The exact trader is required.");
                var able = map.mapPawns.FreeColonistsSpawned.Where(p => !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (able.Count == 0) throw new InvalidOperationException("No colonist can negotiate.");
                var cell = GenAdjFast.AdjacentCells8Way(traderPawn.Position)
                    .FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.Walkable(map));
                if (!cell.IsValid) throw new InvalidOperationException("No standable cell adjacent to the trader.");
                able[0].Position = cell;
                able[0].Notify_Teleported();
                return FixtureKit.Reply(new TradeNegotiators { Success = true, Negotiator = able[0].GetUniqueLoadID(),
                    Other = able.Count > 1 ? able[1].GetUniqueLoadID() : "" });
            }, cancellationToken);
        }

        // Stock and participants only: opening, pricing and transfer are always
        // production trade operations. Coordinates are pinned to the blank lab.
        private static TradeSessionPrepared PrepareSession(Map map, string channel)
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
                return new TradeSessionPrepared { Success = true, Trader = settlement.GetUniqueLoadID(), Caravan = caravan.GetUniqueLoadID(),
                    Negotiator = negotiator.GetUniqueLoadID() };
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
            return new TradeSessionPrepared { Success = true, Trader = ship.GetUniqueLoadID(), Negotiator = negotiator.GetUniqueLoadID() };
        }

        private static TradeRequestPrepared RequestFixture(Map map, string action, string channel, string? pawnId, string? factionId)
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
            else throw new ArgumentException("Unknown request fixture action.");
            var kind = (orbital ? faction.def.orbitalTraderKinds : faction.def.caravanTraderKinds).First(k => k.requestable && k.TitleRequiredToTrade == null);
            return new TradeRequestPrepared { Success = true, Faction = faction.GetUniqueLoadID(), Negotiator = pawn.GetUniqueLoadID(),
                Console = (console ?? throw new InvalidOperationException("Comms console required.")).GetUniqueLoadID(), TraderKind = kind.defName, Goodwill = faction.PlayerGoodwill,
                Cost = -Faction.OfPlayer.CalculateAdjustedGoodwillChange(faction, orbital ? -30 : -15),
                LastRequestTick = orbital ? faction.lastOrbitalTraderRequestTick : faction.lastTraderRequestTick,
                Ally = faction.PlayerRelationKind == FactionRelationKind.Ally };
        }

        private static TradeInventory SessionInventory(Map map, string channel, string? traderId, string? caravanId)
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
            return new TradeInventory { Silver = Count(owned, "Silver"), Medicine = Count(owned, "MedicineIndustrial"), Components = Count(owned, "ComponentIndustrial"),
                SellerComponents = Count(seller.Goods, "ComponentIndustrial"),
                HomeComponents = Count(map.listerThings.AllThings.Where(t => t.Spawned), "ComponentIndustrial") };
        }
    }
}
