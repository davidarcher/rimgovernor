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
            [ToolParameter(Description = "Exclude fixture targets from workers' allowed area.", DefaultValue = false)] bool restrictWorkers = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                try {
                var map = Find.CurrentMap;
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed && !p.Drafted && !p.InMentalState).ToList();
                var origin = GenRadial.RadialCellsAround(people.First().Position, 35, true).First(c =>
                    CellRect.FromLimits(c, c + new IntVec3(8, 0, 8)).Cells.All(p => p.InBounds(map)
                        && !p.Fogged(map) && p.Standable(map) && p.GetEdifice(map) == null
                        && map.zoneManager.ZoneAt(p) == null && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                foreach (var cell in CellRect.FromLimits(origin, origin + new IntVec3(8, 0, 8)))
                    map.areaManager.Home[cell] = true;
                var wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                wall.SetFaction(Faction.OfPlayerSilentFail);
                GenSpawn.Spawn(wall, origin + new IntVec3(3, 0, 3), map);
                wall.TakeDamage(new DamageInfo(DamageDefOf.Blunt, wall.MaxHitPoints / 2f));
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
                foreach (var pawn in people)
                    foreach (var name in new[] { "Hauling", "Cleaning", "Construction", "Firefighter" }) {
                        var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
                        if (work != null && !pawn.WorkTypeIsDisabled(work)) pawn.workSettings.SetPriority(work, 1);
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
                }
                if (restrictWorkers) {
                    Area_Allowed area;
                    if (!map.areaManager.TryMakeNewAllowed(out area)) throw new InvalidOperationException("No fixture allowed-area slot");
                    foreach (var pawn in people) {
                        area[pawn.Position] = true;
                        pawn.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                    }
                    foreach (var cell in new[] { medicine.Position, wall.Position, dirt.Position, storage }) area[cell] = false;
                }
                return new { success = true, medicine = medicine.GetUniqueLoadID(), wall = wall.GetUniqueLoadID(),
                    filth = dirt.GetUniqueLoadID(), fire = fire?.GetUniqueLoadID(), storage = new { x = storage.x, z = storage.z },
                    penAnimal = penAnimal?.GetUniqueLoadID(), looseAnimal = looseAnimal?.GetUniqueLoadID(),
                    pet = pet?.GetUniqueLoadID(), pen = penMarker?.GetUniqueLoadID(),
                    penWall = penWall?.GetUniqueLoadID(), feed = feed?.GetUniqueLoadID(),
                    workers = people.Select(p => p.GetUniqueLoadID()).ToList(),
                    setup = "Spawned damaged wall, covered storage, medicine and dirt; enabled capable workers. Outcomes require ordinary pawn jobs." };
                } catch (Exception error) { return new { success = false, error = error.ToString() }; }
            }, cancellationToken);
        }
    }
}
