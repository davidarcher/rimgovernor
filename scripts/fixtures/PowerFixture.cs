using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Two deterministic power-reliability
    // starting conditions for poweraccept (issue #6 slice 1, milestone A):
    //
    //   fuel    -- one wood-fired generator with its fuel drained, conduits to
    //              one electric consumer, and unforbidden wood nearby. The
    //              consumer is unpowered purely for lack of fuel; refuelling
    //              is ordinary colonist work, so EnsureBasicPower must hold
    //              (waiting_for_refuel) rather than compile a second generator.
    //   reserve -- one fuelled generator, one battery holding a few watt-days,
    //              and consumers drawing more than the generator supplies, so
    //              every consumer is powered right now but the network's
    //              reserve runway is under a day: EnsureBasicPower must admit
    //              one more generator before the battery empties.
    //   battery -- no generator at all: one empty battery, conduits and one
    //              consumer that the bank can no longer carry. Nothing on the
    //              network wants refuelling or repair, so EnsureBasicPower
    //              must add generation rather than wait on the bank (#160).
    //
    // Nothing here refuels, builds or connects anything on the controller's
    // behalf.
    public sealed class PowerFixture
    {
        [Tool("test/power_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: scenario 'fuel' spawns a drained generator, conduits, one consumer and wood; scenario 'reserve' spawns a fuelled generator, a partly charged battery and consumers overdrawing it; scenario 'battery' spawns an empty battery, conduits and one consumer with no generator.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string scenario = "fuel")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (scenario != "fuel" && scenario != "reserve" && scenario != "battery") return Refuse("scenario must be 'fuel', 'reserve' or 'battery'.");
                var builder = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (builder == null) return Refuse("No existing colonist able to construct and haul.");
                // Cooler and generator construction carry a skill prerequisite
                // (Cooler 5, WoodFiredGenerator 4) the controller checks before
                // admitting a build; a random debug colony may have nobody that
                // skilled, so the fixture guarantees one qualified builder.
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 6) { construction.Level = 6; construction.xpSinceLastLevel = 0f; }
                if (builder.workSettings != null && builder.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                    builder.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                Finish(DefDatabase<ResearchProjectDef>.GetNamed("Electricity"));
                var generatorDef = DefDatabase<ThingDef>.GetNamedSilentFail("WoodFiredGenerator");
                var conduitDef = DefDatabase<ThingDef>.GetNamedSilentFail("PowerConduit");
                var batteryDef = DefDatabase<ThingDef>.GetNamedSilentFail("Battery");
                var lampDef = DefDatabase<ThingDef>.GetNamedSilentFail("SunLamp");
                var stoveDef = DefDatabase<ThingDef>.GetNamedSilentFail("ElectricStove");
                if (generatorDef == null || conduitDef == null || batteryDef == null || lampDef == null || stoveDef == null)
                    return Refuse("WoodFiredGenerator, PowerConduit, Battery, SunLamp or ElectricStove unavailable in this ruleset.");
                if (map.listerBuildings.allBuildingsColonist.Any(b => b.TryGetComp<CompPower>() != null))
                    return Refuse("The disposable colony already has power buildings; the fixture needs a single deterministic network.");

                const int width = 14, height = 8;
                // Any open, unfogged, buildable-passability ground will do: plants
                // and loose items are cleared below and the terrain is paved to
                // concrete so wall/cooler heavy-affordance never depends on the
                // debug map's random biome.
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetEdifice(map) == null && cell.GetZone(map) == null
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable 14x8 area for the fixture network.");
                var clearing = new CellRect(origin.x, origin.z, width, height);
                foreach (var cell in clearing.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth).ToList()) thing.Destroy();
                    map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                    map.areaManager.Home[cell] = true;
                }
                IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);
                Thing Spawn(ThingDef def, IntVec3 cell)
                {
                    var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    thing.SetFaction(player);
                    return GenSpawn.Spawn(thing, cell, map, Rot4.North);
                }
                // One full stack per cell on the clearing's far column: a single
                // stack above its def's stackLimit does not spawn as a countable pile.
                var stockCell = 0;
                void Stock(string name, int count)
                {
                    var def = DefDatabase<ThingDef>.GetNamed(name);
                    for (var remaining = count; remaining > 0; remaining -= def.stackLimit)
                    {
                        var stack = ThingMaker.MakeThing(def);
                        stack.stackCount = System.Math.Min(remaining, def.stackLimit);
                        GenSpawn.Spawn(stack, At(12 + stockCell % 2, 1 + stockCell / 2), map);
                        stack.SetForbidden(false, false);
                        stockCell++;
                    }
                }

                // Generator (2x2) at (2,3) unless the scenario has none; conduit
                // line along z=1 from x=1 to x=11.
                Thing generator = scenario == "battery" ? null : Spawn(generatorDef, At(2, 3));
                var fuel = generator?.TryGetComp<CompRefuelable>();
                for (var x = 1; x <= 11; x++) Spawn(conduitDef, At(x, 1));
                var consumers = new List<string>();
                Thing battery = null;
                if (scenario == "fuel")
                {
                    fuel.ConsumeFuel(fuel.Fuel);
                    consumers.Add(Spawn(stoveDef, At(8, 2)).GetUniqueLoadID());
                    Stock("WoodLog", 150);
                }
                else if (scenario == "battery")
                {
                    // The bank is discharged and nothing generates: the consumer
                    // stays off until generation is added, and the stock covers
                    // one wood-fired generator plus its first fuel.
                    battery = Spawn(batteryDef, At(5, 2));
                    battery.TryGetComp<CompPowerBattery>().SetStoredEnergyPct(0f);
                    consumers.Add(Spawn(stoveDef, At(8, 2)).GetUniqueLoadID());
                    Stock("WoodLog", 300);
                    Stock("Steel", 150);
                    Stock("ComponentIndustrial", 6);
                }
                else
                {
                    fuel.Refuel(fuel.Props.fuelCapacity);
                    battery = Spawn(batteryDef, At(5, 2));
                    var stored = battery.TryGetComp<CompPowerBattery>();
                    stored.SetStoredEnergyPct(0f);
                    stored.AddEnergy(300f);
                    // One sun lamp (2900 W) draws far more than one wood generator
                    // (1000 W) makes: 300 Wd of reserve is well under a day.
                    consumers.Add(Spawn(lampDef, At(8, 3)).GetUniqueLoadID());
                    Stock("WoodLog", 300);
                    Stock("Steel", 150);
                    Stock("ComponentIndustrial", 6);
                }
                map.powerNetManager.UpdatePowerNetsAndConnections_First();
                // The game is paused and PowerNet.PowerNetTick re-enables one
                // starved consumer only every 200 ticks, so the reserve start
                // state (every consumer powered, battery covering the shortfall)
                // is set directly: the fixture asserts that, not native ramp-up.
                if (scenario == "reserve")
                    foreach (var consumer in map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompPowerTrader>()).Where(c => c != null && c.Props.PowerConsumption > 0))
                        consumer.PowerOn = true;
                var identity = Current.Game.GetComponent<ColonyIdentity>();
                return new {
                    success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                    tick = Find.TickManager.TicksGame, scenario, builder = builder.GetUniqueLoadID(),
                    origin = new { x = origin.x, z = origin.z },
                    generator = generator?.GetUniqueLoadID(), fuel = fuel?.Fuel, fuelCapacity = fuel?.Props.fuelCapacity,
                    battery = battery?.GetUniqueLoadID(), storedWattDays = battery?.TryGetComp<CompPowerBattery>().StoredEnergy,
                    consumers, spareCell = new { x = At(13, 0).x, z = At(13, 0).z },
                    setup = "Test-only single power network; refuelling, generator construction and reconnection remain the controller's and native colonists' own work.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/power_observe", Description = "Read each comma-separated building id's native power, fuel and battery state. Test-only; changes nothing.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken, string ids = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null) return Refuse("A disposable colony map is required.");
                var rows = new List<object>();
                foreach (var id in (ids ?? "").Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var thing = map.listerThings.AllThings.FirstOrDefault(t => t.GetUniqueLoadID() == id);
                    if (thing == null) { rows.Add(new { id, missing = true }); continue; }
                    var trader = thing.TryGetComp<CompPowerTrader>();
                    var power = thing.TryGetComp<CompPower>();
                    var refuelable = thing.TryGetComp<CompRefuelable>();
                    var stored = thing.TryGetComp<CompPowerBattery>();
                    rows.Add(new {
                        id, defName = thing.def.defName,
                        connected = power?.PowerNet != null, powerOn = trader?.PowerOn,
                        powerOutputW = trader?.PowerOutput,
                        fuel = refuelable?.Fuel, outOfFuel = refuelable != null ? (bool?)!refuelable.HasFuel : null,
                        storedWattDays = stored?.StoredEnergy,
                    });
                }
                var generators = map.listerBuildings.allBuildingsColonist.Where(b => b.def.defName == "WoodFiredGenerator").Select(b => b.GetUniqueLoadID()).ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, buildings = rows, generators };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static void Finish(ResearchProjectDef project)
        {
            if (project.IsFinished) return;
            if (project.prerequisites != null) foreach (var p in project.prerequisites) Finish(p);
            Find.ResearchManager.FinishProject(project, false);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
