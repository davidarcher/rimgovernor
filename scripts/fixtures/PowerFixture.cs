using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only. Deterministic power-reliability
    // starting conditions for poweraccept (issues #6 slice 1 and #418):
    //
    //   fuel       -- one wood-fired generator with its fuel drained, conduits
    //                 to one electric consumer, and unforbidden wood nearby.
    //                 The consumer is unpowered purely for lack of fuel;
    //                 refuelling is ordinary colonist work, so EnsureBasicPower
    //                 must hold (waiting_for_refuel) rather than compile a
    //                 second generator.
    //   reserve    -- one fuelled generator, one battery holding a few
    //                 watt-days, and consumers drawing more than the generator
    //                 supplies, so every consumer is powered right now but the
    //                 network's reserve runway is under a day: EnsureBasicPower
    //                 must admit one more generator before the battery empties.
    //   battery    -- a solar-only network: one solar generator, conduits and
    //                 one lamp inside a small roofed room, no bank at all.
    //                 Nothing carries the night, so EnsureBasicPower must add
    //                 storage (one Battery, indoors) and the lamp must stay
    //                 powered through the night on it.
    //   wind       -- no generator, no bank: one lamp on a conduit line in a
    //                 wide open field (nothing wind-blocking, no roof), with
    //                 Electricity and Batteries researched and no wood in
    //                 stock. EnsureBasicPower must add a WindTurbine whose
    //                 catch zone is clear, then connect it.
    //   geothermal -- no generator, no bank: one lamp on a conduit line and a
    //                 free steam geyser a dozen cells away, with
    //                 GeothermalPower researched. EnsureBasicPower must build
    //                 the GeothermalGenerator on the geyser ahead of any other
    //                 generator, then connect it.
    //
    // Nothing here refuels, builds or connects anything on the controller's
    // behalf.
    public sealed class PowerFixture
    {
        private static readonly string[] Scenarios = { "fuel", "reserve", "battery", "rain", "wind", "geothermal" };

        [Tool("test/power_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: scenario 'fuel' spawns a drained generator, conduits, one consumer and wood; 'reserve' a fuelled generator, a partly charged battery and consumers overdrawing it; 'battery' a solar generator and a lamp in a roofed room with no bank; 'rain' a fuelled generator, a full battery left unroofed and ordinary conduits ahead of forced rain; 'wind' a lamp in a cleared field with no generator; 'geothermal' a lamp and a free steam geyser with no generator.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, string scenario = "fuel")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                if (!Scenarios.Contains(scenario)) return Refuse("scenario must be one of " + string.Join(", ", Scenarios) + ".");
                var builder = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState
                    && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                    .OrderBy(p => p.thingIDNumber).FirstOrDefault();
                if (builder == null) return Refuse("No existing colonist able to construct and haul.");
                // Generator construction carries a skill prerequisite
                // (WoodFiredGenerator 4, WindTurbine 4, GeothermalGenerator 8)
                // the controller checks before admitting a build; a random
                // debug colony may have nobody that skilled, so the fixture
                // guarantees one qualified builder.
                var construction = builder.skills?.GetSkill(SkillDefOf.Construction);
                if (construction != null && construction.Level < 8) { construction.Level = 8; construction.xpSinceLastLevel = 0f; }
                if (builder.workSettings != null && builder.workSettings.GetPriority(WorkTypeDefOf.Construction) == 0)
                    builder.workSettings.SetPriority(WorkTypeDefOf.Construction, 1);
                Finish(DefDatabase<ResearchProjectDef>.GetNamed("Electricity"));
                if (scenario == "battery") { Finish(DefDatabase<ResearchProjectDef>.GetNamed("Batteries")); Finish(DefDatabase<ResearchProjectDef>.GetNamed("SolarPanels")); }
                if (scenario == "wind") Finish(DefDatabase<ResearchProjectDef>.GetNamed("Batteries"));
                if (scenario == "geothermal") Finish(DefDatabase<ResearchProjectDef>.GetNamed("GeothermalPower"));
                var generatorDef = DefDatabase<ThingDef>.GetNamedSilentFail("WoodFiredGenerator");
                var solarDef = DefDatabase<ThingDef>.GetNamedSilentFail("SolarGenerator");
                // Ordinary conduits short-circuit in the wet (#405): every scenario but rain
                // wires with hidden ones so the controller has nothing to replace.
                var conduitDef = DefDatabase<ThingDef>.GetNamedSilentFail(scenario == "rain" ? "PowerConduit" : "HiddenConduit");
                var batteryDef = DefDatabase<ThingDef>.GetNamedSilentFail("Battery");
                var lampDef = DefDatabase<ThingDef>.GetNamedSilentFail("SunLamp");
                var standingLampDef = DefDatabase<ThingDef>.GetNamedSilentFail("StandingLamp");
                var stoveDef = DefDatabase<ThingDef>.GetNamedSilentFail("ElectricStove");
                var wallDef = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
                var doorDef = DefDatabase<ThingDef>.GetNamedSilentFail("Door");
                if (generatorDef == null || solarDef == null || conduitDef == null || batteryDef == null || lampDef == null || standingLampDef == null || stoveDef == null || wallDef == null || doorDef == null)
                    return Refuse("WoodFiredGenerator, SolarGenerator, PowerConduit, Battery, SunLamp, StandingLamp, ElectricStove, Wall or Door unavailable in this ruleset.");
                if (map.listerBuildings.allBuildingsColonist.Any(b => b.TryGetComp<CompPower>() != null))
                    return Refuse("The disposable colony already has power buildings; the fixture needs a single deterministic network.");

                // The wind field must hold a 7x18 catch zone somewhere within a
                // dozen cells of the lamp; the geothermal clearing a 6x6
                // generator beside the conduit line.
                int width = 14, height = 8;
                if (scenario == "wind") { width = 30; height = 30; }
                if (scenario == "geothermal") { width = 24; height = 10; }
                var open = scenario == "wind" || scenario == "geothermal";
                // Any open, unfogged, buildable-passability ground will do: plants
                // and loose items are cleared below and the terrain is paved to
                // concrete so wall/cooler heavy-affordance never depends on the
                // debug map's random biome. The open scenarios also level the
                // natural rock and roof in the clearing but never a built
                // structure: levelling an ancient danger's walls released its
                // dormant mechanoids onto the colony (live 2026-09-19).
                var origin = GenRadial.RadialCellsAround(builder.Position, 75, true).FirstOrDefault(c =>
                    new CellRect(c.x, c.z, width, height).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                        && cell.GetZone(map) == null
                        && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.GetTerrain(map).IsWater
                        && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn
                            || t.def.category == ThingCategory.Building && !(open && t.def.building != null && t.def.building.isNaturalRock)))
                    && builder.CanReach(c, Verse.AI.PathEndMode.Touch, Danger.None));
                if (origin == default) return Refuse("No open reachable " + width + "x" + height + " area for the fixture network.");
                var clearing = new CellRect(origin.x, origin.z, width, height);
                foreach (var cell in clearing.Cells)
                {
                    foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth
                        || open && t.def.category == ThingCategory.Building && t.def.building != null && t.def.building.isNaturalRock).ToList()) thing.Destroy();
                    if (open && map.roofGrid.Roofed(cell)) map.roofGrid.SetRoof(cell, null);
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
                        GenSpawn.Spawn(stack, At(width - 2 + stockCell % 2, 1 + stockCell / 2), map);
                        stack.SetForbidden(false, false);
                        stockCell++;
                    }
                }

                Thing generator = null, battery = null, geyser = null;
                var consumers = new List<string>();
                CompRefuelable fuel = null;
                switch (scenario)
                {
                    case "rain":
                        // Construction must finish before the measured rain exposure.
                        var dry = MakeWeather(600000);
                        dry.weather = DefDatabase<WeatherDef>.GetNamed("Clear");
                        map.gameConditionManager.RegisterCondition(dry);
                        map.weatherManager.TransitionTo(dry.weather);
                        generator = Spawn(generatorDef, At(1, 3));
                        fuel = generator.TryGetComp<CompRefuelable>();
                        for (var x = 1; x <= 11; x++) Spawn(conduitDef, At(x, 1));
                        Spawn(conduitDef, At(1, 2));
                        Spawn(conduitDef, At(5, 2));
                        fuel.Refuel(fuel.Props.fuelCapacity);
                        battery = Spawn(batteryDef, At(5, 3));
                        battery.TryGetComp<CompPowerBattery>().SetStoredEnergyPct(1f);
                        consumers.Add(Spawn(stoveDef, At(10, 3)).GetUniqueLoadID());
                        Stock("WoodLog", 450); Stock("Steel", 150); Stock("ComponentIndustrial", 6);
                        break;
                    case "fuel":
                        // Generator (2x2) at (2,3); conduit line along z=1 from x=1 to x=11.
                        generator = Spawn(generatorDef, At(2, 3));
                        fuel = generator.TryGetComp<CompRefuelable>();
                        for (var x = 1; x <= 11; x++) Spawn(conduitDef, At(x, 1));
                        fuel.ConsumeFuel(fuel.Fuel);
                        consumers.Add(Spawn(stoveDef, At(8, 2)).GetUniqueLoadID());
                        Stock("WoodLog", 150);
                        break;
                    case "reserve":
                        generator = Spawn(generatorDef, At(2, 3));
                        fuel = generator.TryGetComp<CompRefuelable>();
                        for (var x = 1; x <= 11; x++) Spawn(conduitDef, At(x, 1));
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
                        break;
                    case "battery":
                        // A 7x6 wooden room (interior x 1..5, z 1..4) roofed by
                        // hand holds the lamp at (2,2): a battery short-circuits
                        // unroofed in rain or snow, so the controller sites it
                        // indoors within six cells of the lamp. The solar
                        // generator (4x4, anchor (9,2) covers x 8..11, z 1..4)
                        // stands outside on the conduit line along z=2.
                        for (var x = 0; x <= 6; x++) for (var z = 0; z <= 5; z++)
                            if (x == 0 || x == 6 || z == 0 || z == 5) { if (x == 3 && z == 0) Spawn(doorDef, At(x, z)); else Spawn(wallDef, At(x, z)); }
                        for (var x = 0; x <= 6; x++) for (var z = 0; z <= 5; z++) map.roofGrid.SetRoof(At(x, z), RoofDefOf.RoofConstructed);
                        for (var x = 1; x <= 7; x++) Spawn(conduitDef, At(x, 2));
                        consumers.Add(Spawn(standingLampDef, At(2, 2)).GetUniqueLoadID());
                        generator = Spawn(solarDef, At(9, 2));
                        Stock("Steel", 150);
                        Stock("ComponentIndustrial", 6);
                        break;
                    case "wind":
                        // The lamp sits mid-field on a short conduit line; the
                        // turbine's site and its catch zone are the controller's
                        // to find in the cleared 30x30. No wood in stock keeps a
                        // wood-fired generator behind the turbine in the ranking.
                        for (var x = 12; x <= 17; x++) Spawn(conduitDef, At(x, 15));
                        consumers.Add(Spawn(standingLampDef, At(15, 14)).GetUniqueLoadID());
                        Stock("Steel", 200);
                        Stock("ComponentIndustrial", 6);
                        break;
                    case "geothermal":
                        // Conduit line along z=4 from the lamp at (2,4); the
                        // geyser (2x2) at (14,4) leaves the 6x6 generator's
                        // footprint (x 12..17, z 2..7) inside the clearing.
                        for (var x = 1; x <= 6; x++) Spawn(conduitDef, At(x, 4));
                        consumers.Add(Spawn(standingLampDef, At(2, 4)).GetUniqueLoadID());
                        geyser = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.SteamGeyser), At(14, 4), map);
                        Stock("Steel", 400);
                        Stock("ComponentIndustrial", 12);
                        break;
                }
                // Reliability fixtures begin weather-safe: a column supports the
                // roof laid over every building that shorts in rain (the battery
                // room and the open fields need neither); rain starts with only
                // the battery exposed so the controller must build its enclosure.
                // The rain column stands against the stove's roofed row so
                // the roof is held through a roofed path, as a built roof
                // is; two cells away it floats, and the stone-shell census
                // then refuses every shelter wall within support range.
                if (!open && scenario != "battery") Spawn(DefDatabase<ThingDef>.GetNamed("Column"), At(scenario == "rain" ? 11 : 5, scenario == "rain" ? 4 : 5));
                foreach (var building in map.listerBuildings.allBuildingsColonist)
                {
                    if (scenario == "rain" && building == battery) continue;
                    if (!(building.TryGetComp<CompPower>()?.Props.shortCircuitInRain ?? false)) continue;
                    foreach (var cell in building.OccupiedRect().Cells) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                }
                if (scenario == "rain") foreach (var cell in battery.OccupiedRect().Cells) map.roofGrid.SetRoof(cell, null);
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
                    origin = new { x = origin.x, z = origin.z }, width, height,
                    generator = generator?.GetUniqueLoadID(), fuel = fuel?.Fuel, fuelCapacity = fuel?.Props.fuelCapacity,
                    battery = battery?.GetUniqueLoadID(), storedWattDays = battery?.TryGetComp<CompPowerBattery>().StoredEnergy,
                    geyser = geyser?.GetUniqueLoadID(), geyserCell = geyser == null ? null : new { x = geyser.Position.x, z = geyser.Position.z },
                    consumers, spareCell = new { x = At(width - 1, 0).x, z = At(width - 1, 0).z },
                    skyGlow = map.skyManager.CurSkyGlow, hour = GenLocalDate.HourOfDay(map),
                    setup = "Test-only single power network; refuelling, generator construction and reconnection remain the controller's and native colonists' own work.",
                };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/power_observe", Description = "Read each comma-separated building id's native power, fuel and battery state, plus every colonist generator and battery, the sky glow and wind. Test-only; changes nothing.")]
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
                    rows.Add(Row(thing));
                }
                var generators = map.listerBuildings.allBuildingsColonist.Where(b => b.TryGetComp<CompPowerTrader>()?.Props.PowerConsumption < 0).OrderBy(b => b.thingIDNumber).Select(b => b.GetUniqueLoadID()).ToList();
                var batteries = map.listerBuildings.allBuildingsColonist.Where(b => b.TryGetComp<CompPowerBattery>() != null).OrderBy(b => b.thingIDNumber).Select(b => b.GetUniqueLoadID()).ToList();
                var label = "LetterLabelShortCircuit".Translate().CapitalizeFirst().ToString();
                var shortCircuits = Find.Archive.ArchivablesListForReading.OfType<Letter>()
                    .Concat(Find.LetterStack.LettersListForReading).Where(l => l.Label.ToString() == label
                        && l.lookTargets != null && l.lookTargets.targets.Any(t => t.Map == map))
                    .Select(l => l.GetUniqueLoadID()).Distinct().ToList();
                return new { success = true, tick = Find.TickManager.TicksGame, buildings = rows, generators, batteries,
                    skyGlow = map.skyManager.CurSkyGlow, hour = GenLocalDate.HourOfDay(map), windSpeed = map.windManager.WindSpeed,
                    shortCircuits, fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count,
                    eligibleConduits = ShortCircuitUtility.GetShortCircuitablePowerConduits(map).Count(),
                    ordinaryConduits = map.listerThings.ThingsOfDef(ThingDefOf.PowerConduit).Count,
                    rainRate = map.weatherManager.RainRate };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/power_rain", Description = "UNSAFE FOR MODEL EXECUTION. Force Rain for 70000 ticks on the disposable power fixture map; no buildings or stored energy are changed.")]
        public async Task<object> Rain(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) return Refuse("A paused disposable map is required.");
                var rain = DefDatabase<WeatherDef>.GetNamed("Rain");
                foreach (var previous in map.gameConditionManager.ActiveConditions.OfType<GameCondition_ForceWeather>().ToList()) previous.End();
                var condition = MakeWeather(70000);
                condition.weather = rain;
                map.gameConditionManager.RegisterCondition(condition);
                map.weatherManager.TransitionTo(rain);
                return new { success = true, tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static GameCondition_ForceWeather MakeWeather(int duration)
        {
            const string name = "RimGovernorPowerFixtureWeather";
            var def = DefDatabase<GameConditionDef>.GetNamedSilentFail(name);
            if (def == null)
            {
                def = new GameConditionDef { defName = name, label = "power fixture weather", conditionClass = typeof(GameCondition_ForceWeather) };
                DefDatabase<GameConditionDef>.Add(def);
            }
            return (GameCondition_ForceWeather)GameConditionMaker.MakeCondition(def, duration);
        }

        private static object Row(Thing thing)
        {
            var trader = thing.TryGetComp<CompPowerTrader>();
            var power = thing.TryGetComp<CompPower>();
            var refuelable = thing.TryGetComp<CompRefuelable>();
            var stored = thing.TryGetComp<CompPowerBattery>();
            int? windBlocked = null;
            var wind = thing.TryGetComp<CompPowerPlantWind>();
            if (wind != null)
            {
                var field = typeof(CompPowerPlantWind).GetField("windPathBlockedCells", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                windBlocked = (field?.GetValue(wind) as List<IntVec3>)?.Count;
            }
            return new {
                id = thing.GetUniqueLoadID(), defName = thing.def.defName,
                x = thing.Position.x, z = thing.Position.z,
                connected = power?.PowerNet != null, powerNetId = power?.PowerNet?.GetHashCode().ToString(),
                powerOn = trader?.PowerOn, powerOutputW = trader?.PowerOutput,
                fuel = refuelable?.Fuel, outOfFuel = refuelable != null ? (bool?)!refuelable.HasFuel : null,
                storedWattDays = stored?.StoredEnergy, windBlockedCells = windBlocked,
                roofed = thing.OccupiedRect().Cells.All(c => c.Roofed(thing.Map)), hitPoints = thing.HitPoints,
            };
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
