using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable state setup only, excluded from production and the model gateway.
    public sealed class ForecastFixture
    {
        private static Thing generator;
        private static Thing battery;
        private static Thing rice;

        [Tool("test/forecast_setup", Description = "Create a disposable power/food forecast fixture. Test builds only; no construction acceptance.")]
        public async Task<object> Setup(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (generator != null) throw new InvalidOperationException("Fixture already exists");
                var map = Find.CurrentMap;
                var anchor = map.mapPawns.FreeColonistsSpawned.First().Position;
                var origin = GenRadial.RadialCellsAround(anchor, 30, true).First(c =>
                    CellRect.FromLimits(c, c + new IntVec3(8, 0, 8)).Cells.All(p => p.InBounds(map)
                        && !p.Fogged(map) && p.Standable(map) && p.GetEdifice(map) == null
                        && p.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Heavy)));
                Func<string, int, int, Thing> spawn = (name, x, z) => {
                    var thing = ThingMaker.MakeThing(ThingDef.Named(name));
                    thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, origin + new IntVec3(x, 0, z), map);
                    thing.SetForbidden(false, false);
                    return thing;
                };
                generator = spawn("WoodFiredGenerator", 2, 2);
                battery = spawn("Battery", 5, 2);
                var lamp = spawn("StandingLamp", 5, 4);
                for (var x = 2; x <= 5; x++) spawn("PowerConduit", x, 2);
                var fuel = generator.TryGetComp<CompRefuelable>();
                fuel.ConsumeFuel(fuel.Fuel);
                battery.TryGetComp<CompPowerBattery>().AddEnergy(5);
                rice = ThingMaker.MakeThing(ThingDef.Named("RawRice"));
                rice.stackCount = 10;
                GenSpawn.Spawn(rice, origin + new IntVec3(7, 0, 7), map);
                rice.SetForbidden(false, false);
                var rot = rice.TryGetComp<CompRottable>();
                rot.RotProgress = rot.PropsRot.TicksToRotStart - 60;
                var animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Muffalo"), Faction.OfPlayer);
                GenSpawn.Spawn(animal, origin + new IntVec3(7, 0, 5), map);
                var hay = ThingMaker.MakeThing(ThingDef.Named("Hay"));
                hay.stackCount = 50;
                GenSpawn.Spawn(hay, origin + new IntVec3(7, 0, 6), map);
                hay.SetForbidden(false, false);
                var zone = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(zone);
                zone.SetPlantDefToGrow(ThingDef.Named("Plant_Rice"));
                var patch = GenRadial.RadialCellsAround(anchor, 25, true).Where(c => c.InBounds(map)
                    && !c.Fogged(map) && c.GetEdifice(map) == null && c.GetPlant(map) == null
                    && map.zoneManager.ZoneAt(c) == null && map.fertilityGrid.FertilityAt(c) >= 1).Take(4).ToList();
                if (patch.Count != 4) throw new InvalidOperationException("No native fertile crop fixture cells");
                foreach (var cell in patch) zone.AddCell(cell);
                var plant = (Plant)ThingMaker.MakeThing(ThingDef.Named("Plant_Rice"));
                plant.Growth = 1;
                GenSpawn.Spawn(plant, patch[0], map);
                return new { success = true, generator = generator.ThingID, battery = battery.ThingID,
                    lamp = lamp.ThingID, food = rice.GetUniqueLoadID(), foodTemperature = rice.AmbientTemperature,
                    animal = animal.GetUniqueLoadID(), hay = hay.GetUniqueLoadID(), zone = zone.ID,
                    setup = "Spawned test infrastructure, seeded battery reserve and aged rice. Subsequent power/rot progression uses ordinary native ticks." };
            }, cancellationToken);
        }

        [Tool("test/forecast_state", Description = "Read retained fixture rot state even after native destruction. Test builds only.")]
        public async Task<object> State(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (rice == null) throw new InvalidOperationException("Missing fixture food");
                var rot = rice.TryGetComp<CompRottable>();
                return new { success = true, food = rice.GetUniqueLoadID(), destroyed = rice.Destroyed,
                    count = rice.stackCount, rotStage = rot.Stage.ToString(), rotProgress = rot.RotProgress };
            }, cancellationToken);
        }

        [Tool("test/forecast_refuel", Description = "Supply the disposable test generator; test builds only. Subsequent charging is native simulation.")]
        public async Task<object> Refuel(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (generator == null || !generator.Spawned || generator.Map != Find.CurrentMap)
                    throw new InvalidOperationException("Missing fixture generator");
                generator.TryGetComp<CompRefuelable>().Refuel(5);
                return new { success = true, generator = generator.ThingID,
                    storedWd = battery.TryGetComp<CompPowerBattery>().StoredEnergy };
            }, cancellationToken);
        }
    }
}
