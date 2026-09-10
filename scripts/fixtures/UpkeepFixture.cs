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
            [ToolParameter(Description = "Optional disposable fire size; zero omits fire.", DefaultValue = 0f)] float fireSize = 0f)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
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
                var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.GetStoreSettings().filter.SetDisallowAll();
                zone.GetStoreSettings().filter.SetAllow(ThingDefOf.MedicineHerbal, true);
                zone.GetStoreSettings().Priority = StoragePriority.Critical;
                var storage = origin + new IntVec3(4, 0, 3);
                zone.AddCell(storage);
                map.roofGrid.SetRoof(storage, RoofDefOf.RoofConstructed);
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
                return new { success = true, medicine = medicine.GetUniqueLoadID(), wall = wall.GetUniqueLoadID(),
                    filth = dirt.GetUniqueLoadID(), fire = fire?.GetUniqueLoadID(), storage = new { x = storage.x, z = storage.z },
                    setup = "Spawned damaged wall, covered storage, medicine and dirt; enabled capable workers. Outcomes require ordinary pawn jobs." };
            }, cancellationToken);
        }
    }
}
