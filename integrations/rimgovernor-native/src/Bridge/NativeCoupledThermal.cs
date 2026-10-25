using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Native;

internal static class NativeCoupledThermal
{
    /// <summary>
    /// The cells around every site and heater, every heater as a source, and every free colonist's route to every cell
    /// marked to roof (#3341). A colonist's routes run from its own cell, so each is its own search.
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.CoupledThermalRead Read(Map map, Thing[] sites, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var heaters = map.listerThings.AllThings.Where(t => t is Building_Heater or Building_Cooler
            || t is IConstructible && t.def.entityDefToBuild is ThingDef d && (d.thingClass == typeof(Building_Heater) || d.thingClass == typeof(Building_Cooler))
            || Fueled(t is IConstructible ? t.def.entityDefToBuild as ThingDef : t.def))
            .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var cells = new SortedSet<(int X, int Z)>();
        foreach (var origin in sites.Concat(heaters).Select(t => t.Position).Distinct())
        for (var x = origin.x - 6; x <= origin.x + 6; x++)
        for (var z = origin.z - 6; z <= origin.z + 6; z++)
            Add(map, cells, x, z);
        var rows = cells.Select(c => Row(map, c)).ToArray();
        var sources = heaters.Select(t =>
        {
            var definition = t is IConstructible ? (ThingDef)t.def.entityDefToBuild : t.def;
            return definition.thingClass == typeof(Building_Cooler) ? Cooler(map, t.GetUniqueLoadID(), t.Position, t.Rotation, definition, t is Building_Cooler, t.TryGetComp<CompPowerTrader>(), t.TryGetComp<CompTempControl>()?.targetTemperature)
                : Fueled(definition) ? FueledSource(t.GetUniqueLoadID(), t.Position, definition, t is not IConstructible)
                : Source(map, t.GetUniqueLoadID(), t.Position, definition, t is Building_Heater, t.TryGetComp<CompPowerTrader>(),
                    t.TryGetComp<CompTempControl>()?.targetTemperature);
        }).ToArray();
        var roofRoutes = new List<RimGovernor.Brain.GameFacts.Observations.ThermalRoofRoute>();
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        foreach (var cell in rows.Where(c => c.BuildRoof && c.Roof == "none"))
        {
            var ticks = legs.Cost(pawn, pawn.Position, new IntVec3(cell.Cell.X, 0, cell.Cell.Z), PathEndMode.OnCell);
            roofRoutes.Add(new(pawn.GetUniqueLoadID(), cell.Cell, ticks is { } t ? new RimGovernor.Brain.GameFacts.Observations.RoofApproach.Path(t) : new RimGovernor.Brain.GameFacts.Observations.RoofApproach.Unreachable()));
        }
        return Mechanics(map, EquatableArray.From(rows), EquatableArray.From(sources), EquatableArray.From(roofRoutes));
    }

    /// <summary>
    /// An unplaced planned shelter (#3323): its interior and ring cells, and each planned heater as a source of its def at
    /// its planned cell, never complete: a native heater supplied from the network a connector there would join, a fueled
    /// heat pusher (#3673) burning its fuel. Any other def, or a cell off the map, gives no source. Every room and heater
    /// asked is read (#3341).
    /// </summary>
    internal static RimGovernor.Brain.GameFacts.Observations.CoupledThermalRead ReadPlanned(Map map, RimGovernor.Brain.GameOrders.Observations.PlannedThermalRequest request)
    {
        var cells = new SortedSet<(int X, int Z)>();
        foreach (var room in request.Rooms)
        {
            if (room.Width <= 0 || room.Height <= 0) continue;
            for (var x = room.Origin.X - 1; x <= room.Origin.X + room.Width; x++)
            for (var z = room.Origin.Z - 1; z <= room.Origin.Z + room.Height; z++)
                Add(map, cells, x, z);
        }
        var sources = new List<RimGovernor.Brain.GameFacts.Observations.ThermalSource>();
        foreach (var heater in request.Heaters)
        {
            var cell = new IntVec3(heater.Cell.X, 0, heater.Cell.Z);
            if (!cell.InBounds(map) || DefDatabase<ThingDef>.GetNamedSilentFail(heater.Definition) is not { } definition) continue;
            if (Fueled(definition)) sources.Add(FueledSource(heater.Site, cell, definition, false));
            else if (typeof(Building_Heater).IsAssignableFrom(definition.thingClass)) sources.Add(Source(map, heater.Site, cell, definition, false, null, null));
        }
        foreach (var cooler in request.Coolers)
        {
            var room = request.Rooms.Single(r => r.Room == cooler.Room);
            var existing = map.listerThings.AllThings.OfType<Building_Cooler>().OrderBy(c => c.GetUniqueLoadID(), StringComparer.Ordinal)
                .FirstOrDefault(c => { var cold = c.Position + IntVec3.South.RotatedBy(c.Rotation);
                    return cold.x >= room.Origin.X && cold.x < room.Origin.X + room.Width && cold.z >= room.Origin.Z && cold.z < room.Origin.Z + room.Height; });
            if (existing is not null)
            {
                sources.Add(Cooler(map, existing.GetUniqueLoadID(), existing.Position, existing.Rotation, existing.def, true,
                    existing.TryGetComp<CompPowerTrader>(), existing.TryGetComp<CompTempControl>()?.targetTemperature));
                continue;
            }
            var inProgress = map.listerThings.AllThings.Where(t => t is IConstructible && t.def.entityDefToBuild is ThingDef d && d.thingClass == typeof(Building_Cooler))
                .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).FirstOrDefault(t => { var cold = t.Position + IntVec3.South.RotatedBy(t.Rotation);
                    return cold.x >= room.Origin.X && cold.x < room.Origin.X + room.Width && cold.z >= room.Origin.Z && cold.z < room.Origin.Z + room.Height; });
            if (inProgress is not null)
            {
                sources.Add(Cooler(map, inProgress.GetUniqueLoadID(), inProgress.Position, inProgress.Rotation,
                    (ThingDef)inProgress.def.entityDefToBuild, false, null, (float)cooler.TargetTemperature));
                continue;
            }
            var definition = DefDatabase<ThingDef>.GetNamed(cooler.Definition);
            if (definition.thingClass != typeof(Building_Cooler)) throw new InvalidOperationException("Planned cooler is not a Building_Cooler: " + cooler.Definition);
            var options = new List<(IntVec3 Wall, Rot4 Rotation)>();
            for (var x = room.Origin.X; x < room.Origin.X + room.Width; x++)
            for (var z = room.Origin.Z; z < room.Origin.Z + room.Height; z++)
            foreach (var rotation in new[] { Rot4.North, Rot4.East, Rot4.South, Rot4.West })
            {
                var inside = new IntVec3(x, 0, z);
                var wall = inside + IntVec3.North.RotatedBy(rotation);
                var hot = wall + IntVec3.North.RotatedBy(rotation);
                bool Interior(IntVec3 c) => c.x >= room.Origin.X && c.x < room.Origin.X + room.Width && c.z >= room.Origin.Z && c.z < room.Origin.Z + room.Height;
                // VentedWall against the completed planned ring: the wall is occupied, the hot side is open and outside.
                if (Interior(wall) || Interior(hot) || !hot.InBounds(map) || !wall.InBounds(map)
                    || wall.x == room.Door.X && wall.z == room.Door.Z || !hot.Walkable(map)
                    || hot.Roofed(map) && hot.GetRoom(map) is { UsesOutdoorTemperature: false }) continue;
                options.Add((wall, rotation));
            }
            if (options.Count == 0) continue; // A real geometry with no vented wall supports no cooler.
            var chosen = options.OrderBy(o => o.Wall.x).ThenBy(o => o.Wall.z).ThenBy(o => o.Rotation.AsInt).First();
            sources.Add(Cooler(map, "planned/freezer" + cooler.Room + "/cooler", chosen.Wall, chosen.Rotation, definition, false, null, (float)cooler.TargetTemperature));
        }
        return Mechanics(map, EquatableArray.From(cells.Select(c => Row(map, c))), EquatableArray.From(sources), []);
    }

    // Adds an in-bounds cell.
    private static void Add(Map map, SortedSet<(int X, int Z)> cells, int x, int z)
    {
        if (new IntVec3(x, 0, z).InBounds(map)) cells.Add((x, z));
    }

    private static RimGovernor.Brain.GameFacts.Observations.ThermalCell Row(Map map, (int X, int Z) c)
    {
        var cell = new IntVec3(c.X, 0, c.Z); var edifice = cell.GetEdifice(map);
        var site = cell.GetThingList(map).FirstOrDefault(t => t is IConstructible);
        var future = site?.def.entityDefToBuild as ThingDef;
        var door = edifice as Building_Door; var doorDef = door?.def ?? (future?.IsDoor == true ? future : null);
        var roof = cell.GetRoof(map);
        return new RimGovernor.Brain.GameFacts.Observations.ThermalCell(new(c.X, c.Z), cell.GetTemperature(map),
            roof == null ? "none" : roof.isThickRoof ? "thick" : "thin",
            edifice?.def.Fillage == FillCategory.Full, door?.HoldOpen == true,
            site?.GetUniqueLoadID() ?? "", future?.Fillage == FillCategory.Full,
            future?.IsDoor == true, doorDef == null ? 0 : doorDef.building.doorTempEqualizeRate / (double)doorDef.building.doorTempEqualizeIntervalClosed,
            map.areaManager.BuildRoof[cell]);
    }

    // A heater's mechanics at position: built (ready, its own power comp), a blueprint/frame or a planned site (neither).
    private static RimGovernor.Brain.GameFacts.Observations.ThermalSource Source(Map map, string target, IntVec3 position, ThingDef definition, bool ready,
        CompPowerTrader? power, float? targetTemperature)
    {
        var temperature = definition.GetCompProperties<CompProperties_TempControl>()
            ?? throw new InvalidOperationException($"heater def {definition.defName} has no CompProperties_TempControl");
        if (ready && power is null) throw new InvalidOperationException("Built thermal source has no power comp: " + target);
        var watts = (definition.GetCompProperties<CompProperties_Power>() ?? throw new InvalidOperationException("Thermal source has no power properties: " + definition.defName)).PowerConsumption;
        var transmitter = PowerConnectionMaker.BestTransmitterForConnector(position, map);
        var net = power?.PowerNet ?? transmitter?.PowerNet;
        var heat = temperature.energyPerSecond / 60.0;
        var goal = (double)(targetTemperature ?? temperature.defaultTargetTemperature);
        if (net == null) return new(target, new(position.x, position.z), ready, heat, goal, new RimGovernor.Brain.GameFacts.Observations.HeatSupply.Unconnected()) { PowerWatts = watts };
        // No future weather/fuel claim: supply is assessed only from the current network's observed surplus.
        double surplus = net.powerComps.Where(p => p.PowerOn).Sum(p => p.PowerOutput);
        double additional = Math.Max(0, watts + (ready ? power?.PowerOutput ?? 0 : 0));
        var network = string.Join("/", net.powerComps.Select(p => p.parent.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal));
        RimGovernor.Brain.GameFacts.Observations.HeatSupply supply = (ready ? power is { PowerOn: true } && surplus >= additional : surplus >= additional)
            ? new RimGovernor.Brain.GameFacts.Observations.HeatSupply.Powered(network, additional, surplus, RimGovernor.Brain.Domain.GameTime.TicksPerDay)
            : new RimGovernor.Brain.GameFacts.Observations.HeatSupply.ShortOfPower(network, additional, surplus);
        return new(target, new(position.x, position.z), ready, heat, goal, supply) { PowerWatts = watts };
    }

    private static RimGovernor.Brain.GameFacts.Observations.ThermalSource Cooler(Map map, string target, IntVec3 position, Rot4 rotation,
        ThingDef definition, bool ready, CompPowerTrader? power, float? targetTemperature)
    {
        var cold = position + IntVec3.South.RotatedBy(rotation);
        var hot = position + IntVec3.North.RotatedBy(rotation);
        if (!cold.InBounds(map) || !hot.InBounds(map)) throw new InvalidOperationException("Cooler sides are off map: " + target);
        return Source(map, target, position, definition, ready, power, targetTemperature) with
        {
            Cell = new(cold.x, cold.z), HotSideTemperature = hot.GetTemperature(map), Definition = definition.defName,
            BuildCell = new(position.x, position.z), BuildRotation = (RimGovernor.Brain.Domain.Rotation)rotation.AsInt,
        };
    }

    // A def that heats by burning fuel (#3673): a heat pusher with a positive push and a refuelable comp (a campfire).
    private static bool Fueled(ThingDef? definition) =>
        definition?.GetCompProperties<CompProperties_HeatPusher>() is { heatPerSecond: > 0 } && definition.GetCompProperties<CompProperties_Refuelable>() != null;

    // A fueled heat pusher's mechanics (#3673): its push every 60 ticks spread over them, pushing below heatPushMaxTemperature,
    // with unlimited supply. The burn is Burning when it is steady (not use-, power- or externally driven) and of one fuel def:
    // fuelConsumptionRate fuel units a day, an item worth FuelMultiplierCurrentDifficulty units. Otherwise it is UnsteadyBurn.
    private static RimGovernor.Brain.GameFacts.Observations.ThermalSource FueledSource(string target, IntVec3 position, ThingDef definition, bool ready)
    {
        var push = definition.GetCompProperties<CompProperties_HeatPusher>();
        var refuel = definition.GetCompProperties<CompProperties_Refuelable>();
        var fuels = refuel.fuelFilter?.AllowedThingDefs.ToArray() ?? [];
        var steady = !refuel.consumeFuelOnlyWhenUsed && !refuel.consumeFuelOnlyWhenPowered && !refuel.externalTicking
            && fuels.Length == 1 && refuel.FuelMultiplierCurrentDifficulty > 0;
        RimGovernor.Brain.GameFacts.Observations.HeatSupply supply = steady
            ? new RimGovernor.Brain.GameFacts.Observations.HeatSupply.Burning(fuels[0].defName, refuel.fuelConsumptionRate / (double)RimGovernor.Brain.Domain.GameTime.TicksPerDay / refuel.FuelMultiplierCurrentDifficulty)
            : new RimGovernor.Brain.GameFacts.Observations.HeatSupply.UnsteadyBurn();
        return new(target, new(position.x, position.z), ready, push.heatPerSecond / 60.0, push.heatPushMaxTemperature, supply);
    }

    // The map's builders and exchange constants, shared by both reads: every free colonist who can do construction and
    // has a construction speed (one without cannot roof and is no builder).
    private static RimGovernor.Brain.GameFacts.Observations.CoupledThermalRead Mechanics(Map map, EquatableArray<RimGovernor.Brain.GameFacts.Observations.ThermalCell> cells, EquatableArray<RimGovernor.Brain.GameFacts.Observations.ThermalSource> sources,
        EquatableArray<RimGovernor.Brain.GameFacts.Observations.ThermalRoofRoute> roofRoutes)
    {
        var workers = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && p.GetStatValue(StatDefOf.ConstructionSpeed) > 0)
            .Select(p => new RimGovernor.Brain.GameFacts.Observations.ThermalWorker(p.GetUniqueLoadID(), 65.0 / (p.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7))
                { SkillWork = NativeProductiveSkill.ReadWork(p, SkillDefOf.Construction, StatDefOf.ConstructionSpeed,
                    65, 1.7, 0, false, false, "JobDriver_AffectRoof: construction speed, no learning") }).ToArray();
        // The roof rates are private consts, so no typed route reaches them: they are read by name, and a game whose
        // RoomTempTracker lacks one fails the read.
        return new()
        {
            Cells = cells, Sources = sources, RoofRoutes = roofRoutes, Workers = EquatableArray.From(workers),
            Outdoors = map.mapTemperature.OutdoorTemp,
            ThinRoofExchangePerTick = RoofRate("ThinRoofEqualizeRate"),
            NoRoofExchangePerTick = RoofRate("NoRoofEqualizeRate"),
            WallExchangePerTick = (double)RoomTempTracker.WallEqualizeFactor * RoomTempTracker.FractionWallEqualizeCells * RoomTempTracker.EqualizationPowerOfFilledCells,
        };
    }

    private static double RoofRate(string name) =>
        typeof(RoomTempTracker).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue() is float rate
            ? rate : throw new InvalidOperationException($"RoomTempTracker.{name} is not a float constant in this game version");
}
