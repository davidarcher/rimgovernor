using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (#1195). On the blank lab with two colonists
    // it builds two walled, roofed bedrooms north of the centre. Room A, the
    // one artist's (colonist 0, Artistic 8, the other at 3), holds a jade
    // bed, a plant pot, an end table, a dresser and an unpowered lamp, so
    // no lever before the sculpture has anything to add; it stands below the
    // build tier's impressiveness target with beauty its weakest stat. Room
    // B, a plain 3x3 with a granite bed, belongs to an Ascetic neighbour: no
    // target, and already the plainest room, so no bedroom swap moves anyone.
    // Jade (a stuff no floor, pot or bed upgrade beats) and a little silver
    // lie in a stockpile, a sculpting table stands south of the centre, the
    // colony holds no medicine (a purchase need the art may pay for), both
    // colonists carry revolvers (defense capacity, so wealth headroom is
    // positive) and every need is frozen. action=trader brings an exotic
    // goods caravan (a trader that buys art) beside the colonists.
    public sealed class ArtFixture
    {
        private const int JadeCount = 260, SilverCount = 60, TraderSilver = 4000, MealCount = 80;

        // Room interiors (5x5) from the lab centre; walls ring them.
        private static CellRect Interior(Map map, bool artist)
        {
            var c = map.Center;
            return artist ? new CellRect(c.x - 7, c.z + 4, 5, 5) : new CellRect(c.x + 3, c.z + 4, 3, 3);
        }

        [Tool("test/art", Description = "UNSAFE FOR MODEL EXECUTION. Disposable lab fixture (#1195): action=prepare builds an artist's bedroom below its tier target beside an ascetic's plain one, a sculpting table, jade and silver in a stockpile and no medicine; action=trader brings an exotic caravan beside the colonists; action=audit reads both rooms' impressiveness and sculptures, the colony's packed sculptures, silver and the traders' sculptures.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "prepare")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (action == "audit") return Audit(map);
                if (!Find.TickManager.Paused) return new { success = false, reason = "Paused map required." };
                if (action == "trader") return Trader(map);
                if (action != "prepare") throw new ArgumentException("action must be prepare, trader or audit.");
                return Prepare(map);
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Prepare(Map map)
        {
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.ThingID).ToList();
            if (people.Count != 2) return new { success = false, reason = $"Two lab colonists required, found {people.Count}." };
            var center = map.Center;
            var granite = DefDatabase<ThingDef>.GetNamed("BlocksGranite");
            var jade = DefDatabase<ThingDef>.GetNamed("Jade");
            var beds = new List<Building_Bed>();
            for (var i = 0; i < 2; i++)
            {
                var artist = i == 0;
                var inside = Interior(map, artist);
                var shell = inside.ExpandedBy(1);
                var door = new IntVec3(inside.CenterCell.x, 0, shell.minZ);
                foreach (var c in shell.Cells)
                {
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    map.areaManager.Home[c] = true;
                    if (inside.Contains(c)) continue;
                    Place(map, c == door ? ThingDefOf.Door : ThingDefOf.Wall, granite, c, Rot4.North);
                }
                var bed = (Building_Bed)Place(map, ThingDefOf.Bed, artist ? jade : granite, new IntVec3(inside.minX, 0, inside.maxZ - 1), Rot4.North);
                beds.Add(bed);
                if (!artist) continue;
                Place(map, DefDatabase<ThingDef>.GetNamed("PlantPot"), granite, new IntVec3(inside.maxX, 0, inside.maxZ), Rot4.North);
                // The template furniture stands already, so its lever (which
                // goes before the beauty levers) has nothing to add.
                Place(map, DefDatabase<ThingDef>.GetNamed("EndTable"), granite, new IntVec3(inside.minX + 1, 0, inside.maxZ), Rot4.North);
                Place(map, DefDatabase<ThingDef>.GetNamed("Dresser"), granite, new IntVec3(inside.minX + 2, 0, inside.minZ), Rot4.North);
                Place(map, DefDatabase<ThingDef>.GetNamed("StandingLamp"), granite, new IntVec3(inside.minX, 0, inside.minZ), Rot4.North);
            }
            for (var i = 0; i < 2; i++)
            {
                var p = people[i];
                p.ownership.ClaimBedIfNonMedical(beds[i]);
                var art = p.skills.GetSkill(SkillDefOf.Artistic);
                art.Level = i == 0 ? 8 : 3;
                art.passion = Passion.None;
                if (i == 1) p.story.traits.GainTrait(new Trait(DefDatabase<TraitDef>.GetNamed("Ascetic"), 0, true));
                p.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Art"), i == 0 ? 1 : 0);
                if (p.equipment != null && p.equipment.Primary == null)
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_Revolver")));
            }

            // No medicine anywhere: the purchase need art for sale answers.
            foreach (var t in map.listerThings.AllThings.Where(t => t.def.IsMedicine).ToList()) t.Destroy();
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            var zoneRect = new CellRect(center.x - 3, center.z - 5, 6, 3);
            foreach (var c in zoneRect.Cells) { zone.AddCell(c); map.areaManager.Home[c] = true; }
            Stack(map, jade, JadeCount, zoneRect.Cells.First());
            Stack(map, ThingDefOf.Silver, SilverCount, zoneRect.Cells.Last());
            // Food past the Foothold starvation blocker, so development
            // goals (the sale sculpture) are selected; needs are frozen.
            Stack(map, ThingDefOf.MealSurvivalPack, MealCount, zoneRect.CenterCell);
            var table = DefDatabase<ThingDef>.GetNamed("TableSculpting");
            Place(map, table, table.MadeFromStuff ? granite : null, new IntVec3(center.x + 6, 0, center.z - 4), Rot4.North);
            var frozen = FrozenNeeds.Apply(new string[0]);
            return new {
                success = true,
                center = new { x = center.x, z = center.z },
                artist = people[0].ThingID,
                neighbour = people[1].ThingID,
                // The build tier's inputs: the target is its baseline.
                techLevel = Faction.OfPlayer.def.techLevel.ToString(),
                research = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(r => r.IsFinished).Select(r => r.defName).ToList(),
                rooms = Rooms(map),
                frozen,
            };
        }

        private static Thing Place(Map map, ThingDef def, ThingDef stuff, IntVec3 cell, Rot4 rot)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(thing, cell, map, rot);
        }

        private static void Stack(Map map, ThingDef def, int count, IntVec3 cell)
        {
            for (var left = count; left > 0; left -= def.stackLimit)
            {
                var stack = ThingMaker.MakeThing(def);
                stack.stackCount = Math.Min(left, def.stackLimit);
                if (!GenPlace.TryPlaceThing(stack, cell, map, ThingPlaceMode.Near)) throw new InvalidOperationException($"{def.defName} placement failed.");
                stack.SetForbidden(false, false);
            }
        }

        private static bool IsSculpture(ThingDef def) => def.defName.StartsWith("Sculpture");

        private static object Rooms(Map map)
        {
            return new[] { true, false }.Select(artist => {
                var inside = Interior(map, artist);
                var room = inside.CenterCell.GetRoom(map);
                var sculptures = inside.Cells.SelectMany(c => c.GetThingList(map)).Where(t => IsSculpture(t.def)).Distinct().ToList();
                return new {
                    artist,
                    role = room?.Role?.defName,
                    impressiveness = room?.GetStat(RoomStatDefOf.Impressiveness) ?? -1f,
                    beauty = room?.GetStat(RoomStatDefOf.Beauty) ?? -1f,
                    wealth = room?.GetStat(RoomStatDefOf.Wealth) ?? -1f,
                    space = room?.GetStat(RoomStatDefOf.Space) ?? -1f,
                    sculptures = sculptures.Select(t => new { id = t.GetUniqueLoadID(), def = t.def.defName, stuff = t.Stuff?.defName }).ToList(),
                    blueprints = inside.Cells.SelectMany(c => c.GetThingList(map)).Count(t => t is Blueprint_Install),
                };
            }).ToList();
        }

        private static object Audit(Map map)
        {
            // Packed sculptures anywhere the colony holds them: on the ground,
            // in a stockpile or carried.
            var packed = map.listerThings.AllThings.OfType<MinifiedThing>()
                .Concat(map.mapPawns.FreeColonistsSpawned.Select(p => p.carryTracker?.CarriedThing).OfType<MinifiedThing>())
                .Where(m => m.InnerThing != null && IsSculpture(m.InnerThing.def)).Distinct().ToList();
            var traders = map.mapPawns.AllPawnsSpawned.Where(p => p.Faction != null && !p.Faction.IsPlayer && p.GetLord()?.LordJob is LordJob_TradeWithColony).ToList();
            var traderArt = traders.SelectMany(p => p.inventory.innerContainer).OfType<MinifiedThing>().Where(m => m.InnerThing != null && IsSculpture(m.InnerThing.def)).ToList();
            return new {
                success = true,
                tick = Find.TickManager.TicksGame,
                rooms = Rooms(map),
                packed = packed.Select(m => new { id = m.GetUniqueLoadID(), def = m.InnerThing.def.defName, stuff = m.InnerThing.Stuff?.defName,
                    quality = m.InnerThing.TryGetComp<CompQuality>()?.Quality.ToString(), spawned = m.Spawned }).ToList(),
                colonySilver = map.listerThings.ThingsOfDef(ThingDefOf.Silver).Where(t => t.Spawned).Sum(t => t.stackCount),
                jade = map.resourceCounter.GetCount(DefDatabase<ThingDef>.GetNamed("Jade")),
                traders = traders.Count,
                traderArt = traderArt.Select(m => new { id = m.GetUniqueLoadID(), def = m.InnerThing.def.defName }).ToList(),
            };
        }

        private static object Trader(Map map)
        {
            var def = DefDatabase<IncidentDef>.GetNamed("TraderCaravanArrival");
            var kind = DefDatabase<TraderKindDef>.GetNamed("Caravan_Outlander_Exotic");
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.faction = Find.FactionManager.AllFactions.FirstOrDefault(f => !f.IsPlayer && !f.defeated
                && !f.HostileTo(Faction.OfPlayer) && f.def.caravanTraderKinds.Contains(kind));
            if (parms.faction == null) return new { success = false, reason = "No friendly faction runs exotic caravans." };
            parms.traderKind = kind;
            if (!def.Worker.CanFireNow(parms) || !def.Worker.TryExecute(parms)) return new { success = false, reason = "The caravan incident did not fire." };
            var anchor = map.mapPawns.FreeColonistsSpawned.First().Position;
            var traderPawn = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.Faction == parms.faction && p.trader?.traderKind != null);
            if (traderPawn == null) return new { success = false, reason = "No trader pawn arrived." };
            var lord = traderPawn.GetLord();
            // Enough silver to buy the art; the arrival walk is skipped.
            var carrier = lord?.ownedPawns.FirstOrDefault(p => p.GetTraderCaravanRole() == TraderCaravanRole.Carrier) ?? traderPawn;
            var payment = ThingMaker.MakeThing(ThingDefOf.Silver);
            payment.stackCount = TraderSilver;
            carrier.inventory.innerContainer.TryAdd(payment);
            foreach (var p in lord?.ownedPawns ?? new List<Pawn> { traderPawn })
            {
                p.Position = CellFinder.StandableCellNear(anchor, map, 6);
                p.Notify_Teleported();
            }
            var buyer = map.mapPawns.FreeColonistsSpawned.First();
            var buyable = ((ITrader)traderPawn).ColonyThingsWillingToBuy(buyer).OfType<MinifiedThing>()
                .Where(m => m.InnerThing != null && IsSculpture(m.InnerThing.def)).Select(m => m.GetUniqueLoadID()).ToList();
            return new { success = true, traderId = traderPawn.GetUniqueLoadID(), faction = parms.faction.Name, traderKind = kind.defName, buyable };
        }
    }
}
