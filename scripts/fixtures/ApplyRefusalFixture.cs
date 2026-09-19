#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#242, #252). Stages one target per
    // routine write kind and reports the exact snapshot token the controller
    // would have read for each, then moves the world under those tokens on
    // request so apply/refusal can execute each write with a token that was
    // valid when read and assert the named apply-time refusal.
    //
    // prepare: a roofed, walled, empty 2x3 interior (a stockpile zone over its
    // first 2x2, the remaining two cells free roofed ground; one of its walls
    // is the deconstruction target), one forbidden WoodLog stack and one
    // allowed WoodLog stack (the haul target) on open ground, one colonist
    // with hauling enabled, one eligible mature wild plant, one eligible
    // surface rock (also the excavation target), an open cell a wall can be
    // placed on, an empty butcher spot with a cook assigned, a hunter with an
    // ordinary rifle and two wild hares (hunt prey, tame target), and a
    // wooden plant pot; the butcher bill is added after the bench was read.
    // move: fill_cell (a player wall on the cell), delete_zone, allow_item,
    // draft_pawn / undraft_pawn, designate_plant, designate_rock,
    // designate_hunt, designate_tame, suspend_bills, destroy_thing.
    public sealed class ApplyRefusalFixture
    {
        [Tool("test/apply_refusal_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: stage one target per routine write kind (stockpile zone over a roofed interior, forbidden and allowed items, a colonist, a wild plant, a surface rock, a butcher spot, wild hares, a plant pot, a build cell) and report each target's exact snapshot token.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ProtoBoundary.TryReadContext(map, out var context, out var unavailable))
                    return Refuse("Native context unavailable: " + unavailable.Detail);
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                        && p.drafter != null && p.workSettings?.Initialized == true)
                    .OrderBy(p => p.thingIDNumber).ToList();
                var pawn = colonists.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                if (pawn == null) return Refuse("No draftable colonist with hauling enabled.");
                var hunter = colonists.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting) && p.equipment != null);
                if (hunter == null) return Refuse("No colonist able to hunt.");
                var cooking = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
                var cook = cooking == null ? null : colonists.FirstOrDefault(p => !p.WorkTypeIsDisabled(cooking));
                if (cook == null) return Refuse("No colonist able to cook.");

                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                if (wallDef == null || !wallDef.MadeFromStuff || !GenStuff.AllowedStuffsFor(wallDef).Contains(ThingDefOf.WoodLog))
                    return Refuse("Wall def unavailable or WoodLog is not an allowed stuff in this ruleset.");
                var origin = GenRadial.RadialCellsAround(pawn.Position, 40, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, 4, 5).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && cell.GetThingList(map).Count == 0
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                if (origin == default) return Refuse("No open area for the fixture interior.");
                Building? wall = null;
                for (var x = 0; x < 4; x++) for (var z = 0; z < 5; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    if (x == 0 || z == 0 || x == 3 || z == 4)
                    {
                        var built = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                        built.SetFaction(player);
                        GenSpawn.Spawn(built, c, map);
                        if (x == 0 && z == 2) wall = built;
                    }
                    else map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                if (wall == null) return Refuse("Fixture interior has no west wall.");
                var interior = new List<IntVec3>();
                for (var x = 1; x < 3; x++) for (var z = 1; z < 4; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    if (!c.Roofed(map) || c.GetEdifice(map) != null || c.GetThingList(map).Count != 0 || map.zoneManager.ZoneAt(c) != null)
                        return Refuse("Fixture interior cell (" + c.x + "," + c.z + ") is not roofed, empty and unzoned.");
                    interior.Add(c);
                }
                var zoneCells = interior.Where(c => c.z < origin.z + 3).ToList();
                var freeCells = interior.Where(c => c.z >= origin.z + 3).ToList();
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                foreach (var c in zoneCells) zone.AddCell(c);
                if (zone.Cells.Count != 4) return Refuse("Fixture stockpile zone did not take its four cells.");

                // Open picks an unroofed standable cell clear of the interior,
                // of the home area and eight cells from every staged cell:
                // a spawned player building extends Home around itself, which
                // the rock's mining blocker protects.
                var staged = new List<IntVec3>();
                IntVec3 Open(IntVec3 near, int radius, int clearance)
                {
                    var found = GenRadial.RadialCellsAround(near, radius, true).FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map)
                        && c.Standable(map) && c.GetEdifice(map) == null && c.GetZone(map) == null && c.GetThingList(map).Count == 0
                        && !c.Roofed(map) && !map.areaManager.Home[c] && c.DistanceTo(origin) > 8
                        && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)
                        && staged.All(s => s.DistanceTo(c) > clearance));
                    if (found != default) staged.Add(found);
                    return found;
                }
                var itemCell = Open(pawn.Position, 30, 8);
                if (itemCell == default) return Refuse("No open cell for the fixture item.");
                var item = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                item.stackCount = 10;
                GenSpawn.Spawn(item, itemCell, map);
                item.SetForbidden(true, false);
                var itemSnapshot = NativeSupplyAllow.Snapshot(item, context);
                if (itemSnapshot == null) return Refuse("Fixture item is not an eligible forbidden supply.");
                var haulCell = Open(pawn.Position, 30, 8);
                if (haulCell == default) return Refuse("No open cell for the fixture haul target.");
                var haulItem = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                haulItem.stackCount = 10;
                GenSpawn.Spawn(haulItem, haulCell, map);
                haulItem.SetForbidden(false, false);
                var haulSnapshot = NativeSupplyAllow.Snapshot(haulItem, context);
                if (haulSnapshot == null) return Refuse("Fixture haul target is not an eligible supply.");
                var buildCell = Open(pawn.Position, 30, 8);
                if (buildCell == default) return Refuse("No open cell for the fixture build site.");

                var plant = map.listerThings.AllThings.OfType<Plant>()
                    .Where(p => ResourceAcquisitionTools.Eligible(p, map) && !ResourceAcquisitionTools.Designated(p) && p.def.plant.harvestedThingDef != null)
                    .OrderBy(p => p.Position.DistanceTo(pawn.Position)).FirstOrDefault();
                if (plant == null) return Refuse("No eligible undesignated mature wild plant within reach.");
                // The colony's own rocks sit under roof or against home
                // ground, which the mining blocker protects, so the rock is
                // staged on open ground clear of roof, zones and home.
                var rockDef = DefDatabase<ThingDef>.GetNamedSilentFail("Sandstone") ?? DefDatabase<ThingDef>.GetNamedSilentFail("Granite");
                if (rockDef?.building?.mineableThing == null) return Refuse("No mineable rock def in this ruleset.");
                var rockCell = GenRadial.RadialCellsAround(pawn.Position, 30, true).FirstOrDefault(c => c.DistanceTo(origin) > 12 && staged.All(s => s.DistanceTo(c) > 8)
                    && GenRadial.RadialCellsAround(c, 7, true).All(cell => cell.InBounds(map) && !cell.Fogged(map) && !cell.Roofed(map)
                        && !map.roofCollapseBuffer.IsMarkedToCollapse(cell))
                    && GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).Concat(new[] { c }).All(cell => cell.InBounds(map) && cell.Standable(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !map.areaManager.Home[cell]
                        && cell.GetThingList(map).Count == 0));
                if (rockCell == default) return Refuse("No open unroofed cell for the fixture rock.");
                staged.Add(rockCell);
                var rock = (Mineable)ThingMaker.MakeThing(rockDef);
                GenSpawn.Spawn(rock, rockCell, map);
                if (!ResourceAcquisitionTools.Eligible(rock, map) || ResourceAcquisitionTools.Designated(rock))
                    return Refuse("Fixture rock at (" + rockCell.x + "," + rockCell.z + ") is not an eligible mining source: " + (ResourceAcquisitionTools.MiningBlocker(rock, map) ?? "no reaching miner"));

                // Bills and hunt: an empty butcher spot whose token is read
                // before any bill exists, a cook assigned to it, a hunter
                // with an ordinary rifle and a hare within its reach.
                var benchDef = DefDatabase<ThingDef>.GetNamedSilentFail("ButcherSpot");
                var rifleDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_BoltActionRifle");
                var hareKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Hare");
                if (benchDef == null || rifleDef == null || hareKind == null) return Refuse("ButcherSpot, Gun_BoltActionRifle or Hare is unavailable in this ruleset.");
                var benchCell = Open(cook.Position, 30, 8);
                if (benchCell == default) return Refuse("No open cell for the fixture butcher spot.");
                var bench = ThingMaker.MakeThing(benchDef);
                bench.SetFaction(player);
                GenSpawn.Spawn(bench, benchCell, map);
                if (!(bench is IBillGiver giver) || !NativeProductionBills.UsableForNewBill(bench)) return Refuse("Fixture butcher spot is not usable for bills.");
                if (!NativeProductionTracking.Ready) return Refuse("Native production tracking is not ready.");
                cook.workSettings.SetPriority(cooking, 3);
                hunter.workSettings.SetPriority(WorkTypeDefOf.Hunting, 3);
                var prior = hunter.equipment.Primary;
                if (prior != null && !hunter.equipment.TryDropEquipment(prior, out _, hunter.Position, false)) return Refuse("Hunter's equipment could not be dropped.");
                hunter.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(rifleDef));
                var benchToken = NativeProductionBills.Snapshot(bench, giver, context).Token;
                Pawn Hare(IntVec3 cell)
                {
                    var hare = PawnGenerator.GeneratePawn(hareKind, null);
                    GenSpawn.Spawn(hare, cell, map);
                    return hare;
                }
                var preyCell = Open(hunter.Position, 20, 8);
                if (preyCell == default) return Refuse("No open cell for the fixture prey.");
                var prey = Hare(preyCell);
                var tameCell = Open(hunter.Position, 20, 8);
                if (tameCell == default) return Refuse("No open cell for the fixture tame target.");
                var tame = Hare(tameCell);
                if (!NativeHusbandryOperations.Tameable(tame)) return Refuse("Fixture hare is not tameable.");
                // The butcher bill the hunt needs lands after the bench token
                // was read: that is the bill write's moved world.
                var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("ButcherCorpseFlesh");
                if (recipe == null) return Refuse("ButcherCorpseFlesh is unavailable in this ruleset.");
                var butcher = (Bill_Production)recipe.MakeNewBill(null);
                butcher.repeatMode = BillRepeatModeDefOf.Forever;
                giver.BillStack.AddBill(butcher);
                var ineligible = NativeHuntAcquisition.Ineligible(prey);
                if (ineligible != null) return Refuse("Fixture prey is not huntable: " + ineligible);

                var potDef = DefDatabase<ThingDef>.GetNamedSilentFail("PlantPot");
                if (potDef == null) return Refuse("PlantPot is unavailable in this ruleset.");
                var potCell = Open(pawn.Position, 30, 8);
                if (potCell == default) return Refuse("No open cell for the fixture plant pot.");
                var pot = ThingMaker.MakeThing(potDef, potDef.MadeFromStuff ? GenStuff.DefaultStuffFor(potDef) : null);
                pot.SetFaction(player);
                GenSpawn.Spawn(pot, potCell, map);
                var grower = pot as Building_PlantGrower;
                if (grower == null || NativeGrowerCrop.Snapshot(grower, context) == null) return Refuse("Fixture plant pot is not a plant grower.");
                var crop = DefDatabase<ThingDef>.AllDefs.FirstOrDefault(d => NativeGrowerCrop.Sowable(d, grower) && d != grower.GetPlantDefToGrow());
                if (crop == null) return Refuse("No sowable crop for the fixture plant pot.");

                var work = NativeWorkSettings.Snapshot(pawn, context);
                if (work == null) return Refuse("Fixture colonist has no work snapshot.");

                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame,
                    zoneId = zone.GetUniqueLoadID(), zoneToken = NativeZoneObservationTools.Token(zone, context).Token,
                    zoneCells = zoneCells.Select(c => new { x = c.x, z = c.z }).ToList(),
                    freeCells = freeCells.Select(c => new { x = c.x, z = c.z }).ToList(),
                    mapToken = NativeZoneCreation.MapSnapshot(map, context).Token,
                    wallId = wall.GetUniqueLoadID(), wallToken = NativeBuildingObservationTools.Token(wall, context).Token,
                    itemId = item.GetUniqueLoadID(), itemToken = itemSnapshot.Token,
                    haulItemId = haulItem.GetUniqueLoadID(), haulItemToken = haulSnapshot.Token,
                    buildCell = new { x = buildCell.x, z = buildCell.z },
                    pawnId = pawn.GetUniqueLoadID(), pawnWorkToken = work.Token,
                    plantId = plant.GetUniqueLoadID(), plantToken = NativePlantAcquisition.Snapshot(plant, context).Token,
                    plantResource = plant.def.plant.harvestedThingDef.defName, plantCell = new { x = plant.Position.x, z = plant.Position.z },
                    rockId = rock.GetUniqueLoadID(), rockToken = NativeMineAcquisition.Snapshot(rock, context).Token,
                    rockResource = rock.def.building.mineableThing.defName, rockCell = new { x = rock.Position.x, z = rock.Position.z },
                    rockDef = rock.def.defName, excavateToken = NativeExcavationSite.Token(context.Identity, rockCell, rock.def.defName, rock.HitPoints, false),
                    benchId = bench.GetUniqueLoadID(), benchToken,
                    preyId = prey.GetUniqueLoadID(),
                    preyResource = prey.RaceProps.corpseDef.defName, preyCell = new { x = prey.Position.x, z = prey.Position.z },
                    tameId = tame.GetUniqueLoadID(), tameToken = NativeHusbandryOperations.Settings(tame), tameCensusToken = NativeHusbandryOperations.Census(tame),
                    growerId = grower.GetUniqueLoadID(), growerToken = NativeGrowerCrop.Snapshot(grower, context)!.Token, growerCrop = crop.defName,
                    setup = "Test-only staged targets with their read-time snapshot tokens; every write stays native.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/apply_refusal_move", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: move the world under a staged target: fill_cell (x,z), delete_zone (id), allow_item (id), draft_pawn / undraft_pawn (id), designate_plant (id), designate_rock (id), designate_hunt / start_hunt / inspect_hunt (id), designate_tame (id), suspend_bills (id), destroy_thing (id).")]
        public async Task<object> Move(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "", string id = "", int x = -1, int z = -1)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || player == null || !Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
                Thing? Locate(string loadId) => map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == loadId)
                    ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == loadId);
                switch (action)
                {
                    case "fill_cell": {
                        var c = new IntVec3(x, 0, z);
                        if (!c.InBounds(map) || c.GetEdifice(map) != null) return Refuse("Cell is out of bounds or already holds an edifice.");
                        var wall = (Building)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Wall"), ThingDefOf.WoodLog);
                        wall.SetFaction(player);
                        GenSpawn.Spawn(wall, c, map);
                        return new { success = true, edifice = c.GetEdifice(map)?.def.defName };
                    }
                    case "delete_zone": {
                        var zone = map.zoneManager.AllZones.FirstOrDefault(zn => zn.GetUniqueLoadID() == id);
                        if (zone == null) return Refuse("No zone " + id + ".");
                        zone.Delete();
                        return new { success = true, present = map.zoneManager.AllZones.Any(zn => zn.GetUniqueLoadID() == id) };
                    }
                    case "allow_item": {
                        var item = Locate(id);
                        if (item == null) return Refuse("No thing " + id + ".");
                        item.SetForbidden(false, false);
                        return new { success = true, forbidden = item.IsForbidden(player) };
                    }
                    case "draft_pawn":
                    case "undraft_pawn": {
                        if (!(Locate(id) is Pawn pawn) || pawn.drafter == null) return Refuse("No draftable pawn " + id + ".");
                        pawn.drafter.Drafted = action == "draft_pawn";
                        return new { success = true, drafted = pawn.Drafted };
                    }
                    case "designate_plant":
                    case "designate_rock": {
                        var target = Locate(id);
                        if (target == null) return Refuse("No thing " + id + ".");
                        var designator = ResourceAcquisitionTools.DesignatorFor(target);
                        if (target is Mineable) designator.DesignateSingleCell(target.Position); else designator.DesignateThing(target);
                        return new { success = true, designated = ResourceAcquisitionTools.Designated(target) };
                    }
                    case "designate_hunt": {
                        if (!(Locate(id) is Pawn prey)) return Refuse("No animal " + id + ".");
                        new Designator_Hunt().DesignateThing(prey);
                        return new { success = true, designated = NativeHuntAcquisition.Designated(prey) };
                    }
                    case "start_hunt":
                    case "inspect_hunt": {
                        if (!(Locate(id) is Pawn prey)) return Refuse("No animal " + id + ".");
                        if (action == "start_hunt")
                        {
                            var next = GenRadial.RadialCellsAround(prey.Position, 3, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map));
                            var hunter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting) && p.equipment?.Primary?.def.IsRangedWeapon == true);
                            if (hunter == null || !next.IsValid) return Refuse("No hunter or moved prey cell.");
                            prey.Position = next;
                            hunter.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Hunt, prey));
                        }
                        return new { success = true, designated = NativeHuntAcquisition.Designated(prey),
                            hunters = map.mapPawns.FreeColonistsSpawned.Count(p => p.CurJobDef == JobDefOf.Hunt && p.CurJob.targetA.Thing == prey),
                            cell = new { x = prey.Position.x, z = prey.Position.z } };
                    }
                    case "designate_tame": {
                        if (!(Locate(id) is Pawn animal)) return Refuse("No animal " + id + ".");
                        new Designator_Tame().DesignateThing(animal);
                        return new { success = true, designated = map.designationManager.DesignationOn(animal, DesignationDefOf.Tame) != null };
                    }
                    case "suspend_bills": {
                        if (!(Locate(id) is IBillGiver giver)) return Refuse("No bill giver " + id + ".");
                        foreach (var bill in giver.BillStack.Bills) bill.suspended = true;
                        return new { success = true, suspended = giver.BillStack.Bills.Count(b => b.suspended) };
                    }
                    case "destroy_thing": {
                        var thing = Locate(id);
                        if (thing == null) return Refuse("No thing " + id + ".");
                        thing.Destroy(DestroyMode.Vanish);
                        return new { success = true, destroyed = thing.Destroyed };
                    }
                    default:
                        return Refuse("Unknown action " + action + ".");
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
