#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#242). Stages one target per
    // routine write kind and reports the exact snapshot token the controller
    // would have read for each, then moves the world under those tokens on
    // request so apply/refusal can execute each write with a token that was
    // valid when read and assert the named apply-time refusal.
    //
    // prepare: a roofed, walled, empty 2x3 interior (a stockpile zone over its
    // first 2x2, the remaining two cells free roofed ground), one forbidden
    // WoodLog stack on open ground, one colonist with hauling enabled, one
    // eligible mature wild plant and one eligible surface rock.
    // move: fill_cell (a player wall on the cell), delete_zone, allow_item,
    // draft_pawn / undraft_pawn, designate_plant, designate_rock.
    public sealed class ApplyRefusalFixture
    {
        [Tool("test/apply_refusal_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: stage one target per routine write kind (stockpile zone over a roofed interior, a forbidden item, a colonist, a wild plant, a surface rock) and report each target's exact snapshot token.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!ProtoBoundary.TryReadContext(map, out var context, out var unavailable))
                    return Refuse("Native context unavailable: " + unavailable.Detail);
                var pawn = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                        && p.drafter != null && p.workSettings?.Initialized == true && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (pawn == null) return Refuse("No draftable colonist with hauling enabled.");

                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                if (wallDef == null || !wallDef.MadeFromStuff || !GenStuff.AllowedStuffsFor(wallDef).Contains(ThingDefOf.WoodLog))
                    return Refuse("Wall def unavailable or WoodLog is not an allowed stuff in this ruleset.");
                var origin = GenRadial.RadialCellsAround(pawn.Position, 40, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, 4, 5).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.Standable(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && cell.GetThingList(map).Count == 0
                        && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                if (origin == default) return Refuse("No open area for the fixture interior.");
                for (var x = 0; x < 4; x++) for (var z = 0; z < 5; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    if (x == 0 || z == 0 || x == 3 || z == 4)
                    {
                        var wall = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                        wall.SetFaction(player);
                        GenSpawn.Spawn(wall, c, map);
                    }
                    else map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                var interior = new System.Collections.Generic.List<IntVec3>();
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

                var itemCell = GenRadial.RadialCellsAround(pawn.Position, 30, true).FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map)
                    && c.Standable(map) && c.GetEdifice(map) == null && c.GetZone(map) == null && c.GetThingList(map).Count == 0
                    && !c.Roofed(map) && c.DistanceTo(origin) > 8);
                if (itemCell == default) return Refuse("No open cell for the fixture item.");
                var item = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                item.stackCount = 10;
                GenSpawn.Spawn(item, itemCell, map);
                item.SetForbidden(true, false);
                var itemSnapshot = NativeSupplyAllow.Snapshot(item, context);
                if (itemSnapshot == null) return Refuse("Fixture item is not an eligible forbidden supply.");

                var plant = map.listerThings.AllThings.OfType<Plant>()
                    .Where(p => ResourceAcquisitionTools.Eligible(p, map) && !ResourceAcquisitionTools.Designated(p) && p.def.plant.harvestedThingDef != null)
                    .OrderBy(p => p.Position.DistanceTo(pawn.Position)).FirstOrDefault();
                if (plant == null) return Refuse("No eligible undesignated mature wild plant within reach.");
                // The colony's own rocks sit under roof or against home
                // ground, which the mining blocker protects, so the rock is
                // staged on open ground clear of roof, zones and home.
                var rockDef = DefDatabase<ThingDef>.GetNamedSilentFail("Sandstone") ?? DefDatabase<ThingDef>.GetNamedSilentFail("Granite");
                if (rockDef?.building?.mineableThing == null) return Refuse("No mineable rock def in this ruleset.");
                var rockCell = GenRadial.RadialCellsAround(pawn.Position, 30, true).FirstOrDefault(c => c.DistanceTo(origin) > 12 && c.DistanceTo(itemCell) > 3
                    && GenRadial.RadialCellsAround(c, 7, true).All(cell => cell.InBounds(map) && !cell.Fogged(map) && !cell.Roofed(map)
                        && !map.roofCollapseBuffer.IsMarkedToCollapse(cell))
                    && GenAdj.CellsAdjacent8Way(new TargetInfo(c, map)).Concat(new[] { c }).All(cell => cell.InBounds(map) && cell.Standable(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !map.areaManager.Home[cell]
                        && cell.GetThingList(map).Count == 0));
                if (rockCell == default) return Refuse("No open unroofed cell for the fixture rock.");
                var rock = (Mineable)ThingMaker.MakeThing(rockDef);
                GenSpawn.Spawn(rock, rockCell, map);
                if (!ResourceAcquisitionTools.Eligible(rock, map) || ResourceAcquisitionTools.Designated(rock))
                    return Refuse("Fixture rock at (" + rockCell.x + "," + rockCell.z + ") is not an eligible mining source: " + (ResourceAcquisitionTools.MiningBlocker(rock, map) ?? "no reaching miner"));
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
                    itemId = item.GetUniqueLoadID(), itemToken = itemSnapshot.Token,
                    pawnId = pawn.GetUniqueLoadID(), pawnWorkToken = work.Token,
                    plantId = plant.GetUniqueLoadID(), plantToken = NativePlantAcquisition.Snapshot(plant, context).Token,
                    plantResource = plant.def.plant.harvestedThingDef.defName, plantCell = new { x = plant.Position.x, z = plant.Position.z },
                    rockId = rock.GetUniqueLoadID(), rockToken = NativeMineAcquisition.Snapshot(rock, context).Token,
                    rockResource = rock.def.building.mineableThing.defName, rockCell = new { x = rock.Position.x, z = rock.Position.z },
                    setup = "Test-only staged targets with their read-time snapshot tokens; every write stays native.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/apply_refusal_move", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: move the world under a staged target: fill_cell (x,z), delete_zone (id), allow_item (id), draft_pawn / undraft_pawn (id), designate_plant (id), designate_rock (id).")]
        public async Task<object> Move(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "", string id = "", int x = -1, int z = -1)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || player == null || !Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
                Thing? Locate(string loadId) => map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == loadId);
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
                    default:
                        return Refuse("Unknown action " + action + ".");
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
