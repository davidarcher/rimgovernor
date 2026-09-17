using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable scenario preparation; never included in production builds.
    public sealed class UpkeepFixture
    {
        [Tool("test/upkeep_setup", Description = "Prepare disposable hauling, repair and cleaning targets. Test builds only.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Optional disposable fire size; zero omits fire.", DefaultValue = 0f)] float fireSize = 0f,
            [ToolParameter(Description = "Include a pen animal, pet and stored feed.", DefaultValue = false)] bool animals = false,
            [ToolParameter(Description = "Leave covered space unzoned for the storage method.", DefaultValue = false)] bool storageMissing = false,
            [ToolParameter(Description = "Wall off the fixture site so no worker can reach its targets.", DefaultValue = false)] bool restrictWorkers = false,
            [ToolParameter(Description = "Include a more damaged cosmetic repair target.", DefaultValue = false)] bool repairCompetition = false,
            [ToolParameter(Description = "Clear disposable loose items and filth before preparing bounded read censuses. Use only after gameplay assertions.", DefaultValue = false)] bool boundedCensus = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                try {
                var map = Find.CurrentMap;
                int clearedItems = 0, clearedFilth = 0;
                if (boundedCensus) {
                    foreach (var thing in map.listerThings.AllThings.Where(t => t.Spawned &&
                        (t.def.category == ThingCategory.Item || t is Filth)).ToList()) {
                        if (thing is Filth) clearedFilth++; else clearedItems++;
                        thing.Destroy();
                    }
                }
                // Starting colonists may arrive injured; an untended patient
                // is a priority-1 medical emergency that holds the clock and
                // every development row, so the upkeep deficits under test
                // could never be reviewed. Healthy workers are a precondition.
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                    foreach (var h in p.health.hediffSet.hediffs.Where(h => (h.def.tendable || h.def.isBad) && !(h is Hediff_MissingPart)).ToList())
                        p.health.RemoveHediff(h);
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState).ToList();
                // Dense rolls (Scarlands, mountainous maps) rarely offer a
                // clear 9x9 within 35 cells; search to the radial pattern's
                // limit, nearest first, and refuse so a fresh map is a
                // rerun, never an exception.
                var origin = GenRadial.RadialCellsAround(people.First().Position, 55, true).FirstOrDefault(c =>
                    CellRect.FromLimits(c, c + new IntVec3(8, 0, 8)).Cells.All(p => p.InBounds(map)
                        && !p.Fogged(map) && p.Standable(map) && p.GetEdifice(map) == null
                        && map.zoneManager.ZoneAt(p) == null && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)
                        // Standable cells still carry loose items (the scenario's
                        // dropped starting gear); one on the storage cell keeps the
                        // medicine unstorable and every haul refused.
                        && !p.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)));
                if (!origin.IsValid)
                    return new { success = false, error = "no clear 9x9 heavy-affordance site within 55 cells of the colonists; reroll the map" };
                foreach (var cell in CellRect.FromLimits(origin, origin + new IntVec3(8, 0, 8)))
                    map.areaManager.Home[cell] = true;
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(wall, origin + new IntVec3(3, 0, 3), map);
                wall.TakeDamage(new DamageInfo(DamageDefOf.Blunt, wall.MaxHitPoints / 2f));
                Thing cosmetic = null;
                if (repairCompetition) {
                    cosmetic = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                    cosmetic.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(cosmetic, origin + new IntVec3(1, 0, 3), map);
                    cosmetic.HitPoints = System.Math.Max(1, cosmetic.MaxHitPoints / 10);
                }
                var storage = origin + new IntVec3(4, 0, 3);
                if (!storageMissing) {
                    var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                    map.zoneManager.RegisterZone(zone);
                    zone.GetStoreSettings().filter.SetDisallowAll();
                    zone.GetStoreSettings().filter.SetAllow(ThingDefOf.MedicineHerbal, true);
                    zone.GetStoreSettings().Priority = StoragePriority.Critical;
                    zone.AddCell(storage);
                }
                foreach (var cell in CellRect.FromLimits(storage, storage + new IntVec3(1, 0, 1))) {
                    if (storageMissing)
                        foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item).ToList())
                            thing.Destroy();
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                }
                var medicine = ThingMaker.MakeThing(ThingDefOf.MedicineHerbal);
                medicine.stackCount = 5;
                GenSpawn.Spawn(medicine, origin + new IntVec3(1, 0, 1), map);
                medicine.SetForbidden(false, false);
                var dirt = ThingMaker.MakeThing(ThingDefOf.Filth_Dirt);
                GenSpawn.Spawn(dirt, origin + new IntVec3(2, 0, 3), map);
                // Existing dirt, beyond the installed native cleaning cooldown.
                BridgeCommon.PrivateInstanceField(typeof(Filth), "growTick").SetValue(dirt, Find.TickManager.TicksGame - 1200);
                Fire fire = null;
                if (fireSize > 0) {
                    fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
                    fire.fireSize = fireSize;
                    GenSpawn.Spawn(fire, origin + new IntVec3(5, 0, 5), map);
                }
                // Workers stay capable but their Work-tab priorities for the
                // staged deficits are cleared, so only the controller's forced
                // orders (which ignore priorities, like the float menu) act on
                // the medicine, wall and dirt; otherwise ordinary colonist
                // hauling races the SecureSupplies dispatch and the harness
                // cannot tell whose job recovered the deficit. Firefighting
                // stays on: MaintainFireSafety is observed, never ordered.
                foreach (var pawn in people)
                    foreach (var name in new[] { "Hauling", "Cleaning", "Construction", "Firefighter" }) {
                        var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
                        if (work != null && !pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, name == "Firefighter" ? 1 : 0);
                    }
                Pawn penAnimal = null, pet = null, looseAnimal = null;
                Thing penMarker = null, penWall = null, feed = null;
                if (animals) {
                    var rect = CellRect.FromLimits(origin + new IntVec3(4, 0, 4), origin + new IntVec3(8, 0, 8));
                    foreach (var cell in rect.EdgeCells) {
                        var fence = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                        fence.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(fence, cell, map);
                        penWall = fence;
                    }
                    penMarker = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("PenMarker"), ThingDefOf.WoodLog);
                    penMarker.SetFaction(Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(penMarker, origin + new IntVec3(6, 0, 6), map);
                    penAnimal = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), Faction.OfPlayerSilentFail);
                    pet = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Husky"), Faction.OfPlayerSilentFail);
                    looseAnimal = PawnGenerator.GeneratePawn(DefDatabase<PawnKindDef>.GetNamed("Muffalo"), Faction.OfPlayerSilentFail);
                    GenSpawn.Spawn(penAnimal, origin + new IntVec3(5, 0, 6), map);
                    GenSpawn.Spawn(pet, origin, map);
                    GenSpawn.Spawn(looseAnimal, origin + new IntVec3(1, 0, 0), map);
                    feed = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Hay"));
                    feed.stackCount = 20;
                    GenSpawn.Spawn(feed, origin + new IntVec3(6, 0, 5), map);
                    feed.SetForbidden(false, false);
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                }
                int ringWalls = 0;
                if (restrictWorkers) {
                    // An allowed-area restriction is no blocker: the controller's
                    // orders are player-forced, and forced jobs ignore the area
                    // like a right-click order does. Only pathing stops them, so
                    // ring the site with walls and move any colonist standing
                    // inside it out, leaving every target unreachable.
                    var site = CellRect.FromLimits(origin, origin + new IntVec3(8, 0, 8));
                    var ring = site.ExpandedBy(1);
                    foreach (var cell in ring.EdgeCells) {
                        // An existing edifice (rock, wall) already blocks; a
                        // tree only slows pawns down, so it is felled first.
                        if (!cell.InBounds(map) || cell.GetEdifice(map) != null) continue;
                        foreach (var plant in cell.GetThingList(map).Where(t => t is Plant).ToList()) plant.Destroy();
                        var brick = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                        brick.SetFaction(Faction.OfPlayerSilentFail);
                        GenSpawn.Spawn(brick, cell, map);
                        ringWalls++;
                    }
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned.Where(p => ring.Contains(p.Position)).ToList()) {
                        var outside = GenRadial.RadialCellsAround(ring.CenterCell, 40, true).FirstOrDefault(c =>
                            c.InBounds(map) && !ring.Contains(c) && c.Standable(map) && !c.Fogged(map));
                        if (!outside.IsValid) throw new InvalidOperationException("No standable cell outside the walled fixture site");
                        pawn.Position = outside;
                        pawn.Notify_Teleported();
                    }
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                }
                return new { success = true, clearedItems, clearedFilth, ringWalls, medicine = medicine.GetUniqueLoadID(), wall = wall.GetUniqueLoadID(),
                    cosmetic = cosmetic?.GetUniqueLoadID(),
                    filth = dirt.GetUniqueLoadID(), fire = fire?.GetUniqueLoadID(), storage = new { x = storage.x, z = storage.z },
                    penAnimal = penAnimal?.GetUniqueLoadID(), looseAnimal = looseAnimal?.GetUniqueLoadID(),
                    pet = pet?.GetUniqueLoadID(), pen = penMarker?.GetUniqueLoadID(),
                    penWall = penWall?.GetUniqueLoadID(), feed = feed?.GetUniqueLoadID(),
                    workers = people.Select(p => p.GetUniqueLoadID()).ToList(),
                    setup = "Spawned damaged wall, covered storage, medicine and dirt; capable workers with hauling/cleaning/construction priorities cleared. Outcomes require controller-ordered pawn jobs." };
                } catch (Exception error) { return new { success = false, error = error.ToString() }; }
            }, cancellationToken);
        }
    }
}
