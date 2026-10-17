#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Stages one target per
    // routine write kind apply/refusal moves the world under, then moves
    // the world under those targets on request so the case can execute each
    // write after its precondition was read and assert the named apply-time
    // refusal.
    //
    // prepare: a roofed, walled, empty 2x3 interior (a stockpile zone over its
    // first 2x2, the remaining two cells free roofed ground), one allowed
    // WoodLog stack (the haul target) on open ground, one colonist with
    // hauling enabled, a wild hare (the tame target), a wooden plant pot and
    // an open cell a building can be placed on.
    // move: fill_cell (a player wall on the cell), delete_zone,
    // draft_pawn / undraft_pawn, designate_hunt, destroy_thing.
    public sealed class ApplyRefusalFixture
    {
        [Tool("test/apply_refusal_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: stage one target per routine write kind apply/refusal moves (stockpile zone over a roofed interior, a haul item, a colonist, a wild hare, a plant pot, a build cell).")]
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
                // Ground is any standable heavy-affordance cell without an
                // edifice or zone; whatever wild plants, items and filth
                // stand on it are cleared before the fixture uses it.
                bool Ground(IntVec3 cell) => cell.InBounds(map) && !cell.Fogged(map) && cell.Standable(map)
                    && cell.GetEdifice(map) == null && cell.GetZone(map) == null && cell.GetFirstPawn(map) == null
                    && cell.GetThingList(map).All(t => t is Plant || t is Filth || t.def.category == ThingCategory.Item)
                    && cell.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy);
                void Clear(IntVec3 cell)
                {
                    foreach (var t in cell.GetThingList(map).Where(t => t is Plant || t is Filth || t.def.category == ThingCategory.Item).ToList())
                        t.Destroy(DestroyMode.Vanish);
                }
                // The interior is sought within 40 cells of the colonist,
                // then anywhere on the map nearest first.
                var rectFits = new Func<IntVec3, bool>(c => new CellRect(c.x, c.z, 4, 5).Cells.All(Ground));
                var origin = GenRadial.RadialCellsAround(pawn.Position, 40, true).FirstOrDefault(rectFits);
                if (origin == default) origin = map.AllCells.OrderBy(c => c.DistanceToSquared(pawn.Position)).FirstOrDefault(rectFits);
                if (origin == default) return Refuse("No open area for the fixture interior.");
                for (var x = 0; x < 4; x++) for (var z = 0; z < 5; z++)
                {
                    var c = new IntVec3(origin.x + x, 0, origin.z + z);
                    Clear(c);
                    if (x == 0 || z == 0 || x == 3 || z == 4)
                    {
                        var built = (Building)ThingMaker.MakeThing(wallDef, ThingDefOf.WoodLog);
                        built.SetFaction(player);
                        GenSpawn.Spawn(built, c, map);
                    }
                    else map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
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
                // of the home area and eight cells from every staged cell.
                var staged = new List<IntVec3>();
                IntVec3 Open(int radius)
                {
                    var found = GenRadial.RadialCellsAround(pawn.Position, radius, true).FirstOrDefault(c => Ground(c)
                        && !c.Roofed(map) && !map.areaManager.Home[c] && c.DistanceTo(origin) > 8
                        && staged.All(s => s.DistanceTo(c) > 8));
                    if (found != default) { staged.Add(found); Clear(found); }
                    return found;
                }
                var haulCell = Open(30);
                if (haulCell == default) return Refuse("No open cell for the fixture haul target.");
                var haulItem = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                haulItem.stackCount = 10;
                GenSpawn.Spawn(haulItem, haulCell, map);
                haulItem.SetForbidden(false, false);
                if (NativeSupplyAllow.Snapshot(haulItem, context) == null) return Refuse("Fixture haul target is not an eligible supply.");
                var buildCell = Open(30);
                if (buildCell == default) return Refuse("No open cell for the fixture build site.");

                var hareKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Hare");
                if (hareKind == null) return Refuse("Hare is unavailable in this ruleset.");
                var tameCell = Open(20);
                if (tameCell == default) return Refuse("No open cell for the fixture tame target.");
                var tame = PawnGenerator.GeneratePawn(hareKind, null);
                GenSpawn.Spawn(tame, tameCell, map);
                if (!NativeHusbandryOperations.Tameable(tame)) return Refuse("Fixture hare is not tameable.");

                var potDef = DefDatabase<ThingDef>.GetNamedSilentFail("PlantPot");
                if (potDef == null) return Refuse("PlantPot is unavailable in this ruleset.");
                var potCell = Open(30);
                if (potCell == default) return Refuse("No open cell for the fixture plant pot.");
                var pot = ThingMaker.MakeThing(potDef, potDef.MadeFromStuff ? GenStuff.DefaultStuffFor(potDef) : null);
                pot.SetFaction(player);
                GenSpawn.Spawn(pot, potCell, map);
                var grower = pot as Building_PlantGrower;
                if (grower == null || NativeGrowerCrop.Snapshot(grower, context) == null) return Refuse("Fixture plant pot is not a plant grower.");
                var crop = DefDatabase<ThingDef>.AllDefs.FirstOrDefault(d => NativeGrowerCrop.Sowable(d, grower) && d != grower.GetPlantDefToGrow());
                if (crop == null) return Refuse("No sowable crop for the fixture plant pot.");

                FixtureCell Cell(IntVec3 c) => new FixtureCell { X = c.x, Z = c.z };
                return FixtureKit.Reply(new ApplyRefusalPrepared
                {
                    Success = true,
                    ZoneId = zone.GetUniqueLoadID(),
                    ZoneCells = zoneCells.Select(Cell).ToList(),
                    FreeCells = freeCells.Select(Cell).ToList(),
                    BuildCell = Cell(buildCell),
                    HaulItemId = haulItem.GetUniqueLoadID(),
                    PawnId = pawn.GetUniqueLoadID(),
                    TameId = tame.GetUniqueLoadID(),
                    GrowerId = grower.GetUniqueLoadID(),
                    GrowerCrop = crop.defName,
                });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/apply_refusal_move", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: move the world under a staged target: fill_cell (x,z), delete_zone (id), draft_pawn / undraft_pawn (id), designate_hunt (id), destroy_thing (id).")]
        public async Task<object> Move(IRimBridgeContext ctx, CancellationToken cancellationToken, string action = "", string id = "", int x = -1, int z = -1)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || player == null || !Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
                Thing? Locate(string loadId) => RefIndex.Thing(map, loadId)
                    ?? map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == loadId);
                switch (action)
                {
                    case "fill_cell": {
                        var c = new IntVec3(x, 0, z);
                        if (!c.InBounds(map) || c.GetEdifice(map) != null) return Refuse("Cell is out of bounds or already holds an edifice.");
                        var wall = (Building)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Wall"), ThingDefOf.WoodLog);
                        wall.SetFaction(player);
                        GenSpawn.Spawn(wall, c, map);
                        return Done();
                    }
                    case "delete_zone": {
                        var zone = map.zoneManager.AllZones.FirstOrDefault(zn => zn.GetUniqueLoadID() == id);
                        if (zone == null) return Refuse("No zone " + id + ".");
                        zone.Delete();
                        return Done();
                    }
                    case "draft_pawn":
                    case "undraft_pawn": {
                        if (!(Locate(id) is Pawn pawn) || pawn.drafter == null) return Refuse("No draftable pawn " + id + ".");
                        pawn.drafter.Drafted = action == "draft_pawn";
                        return Done();
                    }
                    case "designate_hunt": {
                        if (!(Locate(id) is Pawn prey)) return Refuse("No animal " + id + ".");
                        new Designator_Hunt().DesignateThing(prey);
                        return NativeHuntAcquisition.Designated(prey) ? Done() : Refuse("The hunt designation did not land on " + id + ".");
                    }
                    case "destroy_thing": {
                        var thing = Locate(id);
                        if (thing == null) return Refuse("No thing " + id + ".");
                        thing.Destroy(DestroyMode.Vanish);
                        return Done();
                    }
                    default:
                        return Refuse("Unknown action " + action + ".");
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Done() => FixtureKit.Reply(new FixtureDone { Success = true });

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
