#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace HomeBridge.BridgeTools;

internal static class NativeCoupledThermal
{
    /// <summary>
    /// The cells around every site and heater, every heater as a source, and every free colonist's route to every cell
    /// marked to roof (#3341). A colonist's routes run from its own cell, so each is its own search.
    /// </summary>
    internal static Obs.CoupledThermalRead Read(Map map, Thing[] sites, NativePathLegs? legs = null)
    {
        legs ??= new(map);
        var heaters = map.listerThings.AllThings.Where(t => t is Building_Heater
            || t is IConstructible && t.def.entityDefToBuild is ThingDef d && d.thingClass == typeof(Building_Heater))
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
            return Source(map, t.GetUniqueLoadID(), t.Position, definition, t is Building_Heater, t.TryGetComp<CompPowerTrader>(),
                t.TryGetComp<CompTempControl>()?.targetTemperature);
        }).ToArray();
        var roofRoutes = new List<Obs.ThermalRoofRoute>();
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        foreach (var cell in rows.Where(c => c.BuildRoof && c.Roof == "none"))
        {
            var ticks = legs.Cost(pawn, pawn.Position, new IntVec3(cell.Cell.X, 0, cell.Cell.Z), PathEndMode.OnCell);
            roofRoutes.Add(new(pawn.GetUniqueLoadID(), cell.Cell, ticks is { } t ? Fact.Known((double)t) : default));
        }
        return Mechanics(map) with { Cells = EquatableArray.From(rows), Sources = EquatableArray.From(sources),
            RoofRoutes = EquatableArray.From(roofRoutes) };
    }

    /// <summary>
    /// An unplaced planned shelter (#3323): its interior and ring cells, and each planned heater as a source of its def at
    /// its planned cell, never complete, supplied from the network a connector there would join. A def that is not a
    /// native heater, or a cell off the map, gives no source. Every room and heater asked is read (#3341).
    /// </summary>
    internal static Obs.CoupledThermalRead ReadPlanned(Map map, Obs.PlannedThermalRequest request)
    {
        var cells = new SortedSet<(int X, int Z)>();
        foreach (var room in request.Rooms)
        {
            if (room.Width <= 0 || room.Height <= 0) continue;
            for (var x = room.Origin.X - 1; x <= room.Origin.X + room.Width; x++)
            for (var z = room.Origin.Z - 1; z <= room.Origin.Z + room.Height; z++)
                Add(map, cells, x, z);
        }
        var sources = new List<Obs.ThermalSource>();
        foreach (var heater in request.Heaters)
        {
            var cell = new IntVec3(heater.Cell.X, 0, heater.Cell.Z);
            if (!cell.InBounds(map) || DefDatabase<ThingDef>.GetNamedSilentFail(heater.Definition) is not { } definition
                || !typeof(Building_Heater).IsAssignableFrom(definition.thingClass)) continue;
            sources.Add(Source(map, heater.Site, cell, definition, false, null, null));
        }
        return Mechanics(map) with { Cells = EquatableArray.From(cells.Select(c => Row(map, c))), Sources = EquatableArray.From(sources) };
    }

    // Adds an in-bounds cell.
    private static void Add(Map map, SortedSet<(int X, int Z)> cells, int x, int z)
    {
        if (new IntVec3(x, 0, z).InBounds(map)) cells.Add((x, z));
    }

    private static Obs.ThermalCell Row(Map map, (int X, int Z) c)
    {
        var cell = new IntVec3(c.X, 0, c.Z); var edifice = cell.GetEdifice(map);
        var site = cell.GetThingList(map).FirstOrDefault(t => t is IConstructible);
        var future = site?.def.entityDefToBuild as ThingDef;
        var door = edifice as Building_Door; var doorDef = door?.def ?? (future?.IsDoor == true ? future : null);
        var roof = cell.GetRoof(map);
        return new Obs.ThermalCell(new(c.X, c.Z), Fact.Known((double)cell.GetTemperature(map)),
            roof == null ? "none" : roof.isThickRoof ? "thick" : "thin",
            edifice?.def.Fillage == FillCategory.Full, door != null, door != null && !door.Open,
            site?.GetUniqueLoadID() ?? "", future?.Fillage == FillCategory.Full,
            future?.IsDoor == true, doorDef == null ? 0 : doorDef.building.doorTempEqualizeRate / (double)doorDef.building.doorTempEqualizeIntervalClosed,
            map.areaManager.BuildRoof[cell]);
    }

    // A heater's mechanics at position: built (ready, its own power comp), a blueprint/frame or a planned site (neither).
    private static Obs.ThermalSource Source(Map map, string target, IntVec3 position, ThingDef definition, bool ready,
        CompPowerTrader? power, float? targetTemperature)
    {
        var temperature = definition.GetCompProperties<CompProperties_TempControl>();
        var transmitter = PowerConnectionMaker.BestTransmitterForConnector(position, map);
        var net = power?.PowerNet ?? transmitter?.PowerNet;
        var watts = definition.GetCompProperties<CompProperties_Power>()?.PowerConsumption ?? 0;
        // No future weather/fuel claim: supply is assessed only from the current network's observed surplus.
        var surplus = net == null ? 0 : net.powerComps.Where(p => p.PowerOn).Sum(p => p.PowerOutput);
        var additional = Math.Max(0, watts + (ready ? power?.PowerOutput ?? 0 : 0));
        Fact<double> supply = net != null && (ready ? power?.PowerOn == true && surplus >= additional : surplus >= additional)
            ? Fact.Known((double)RimGovernor.Brain.Domain.GameTime.TicksPerDay) : default;
        return new Obs.ThermalSource(target, new(position.x, position.z), ready,
            temperature == null ? default : Fact.Known(temperature.energyPerSecond / 60.0),
            Fact.Known((double)(targetTemperature ?? temperature?.defaultTargetTemperature ?? 20)), supply)
            { PowerNetwork = net == null ? "" : string.Join("/", net.powerComps.Select(p => p.parent.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal)),
                AdditionalWatts = Fact.Known((double)additional), SurplusWatts = net == null ? default : Fact.Known((double)surplus) };
    }

    // The map's builders and exchange constants, shared by both reads.
    private static Obs.CoupledThermalRead Mechanics(Map map)
    {
        var workers = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).Select(p => new Obs.ThermalWorker(p.GetUniqueLoadID(),
                p.GetStatValue(StatDefOf.ConstructionSpeed) > 0
                    ? Fact.Known(65.0 / (p.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7)) : default)).ToArray();
        // Constants are read from this game's mechanics; unsupported versions produce unknown observations.
        var thin = typeof(RoomTempTracker).GetField("ThinRoofEqualizeRate", BindingFlags.Static | BindingFlags.NonPublic);
        return new() { Workers = EquatableArray.From(workers), Outdoors = Fact.Known((double)map.mapTemperature.OutdoorTemp),
            ThinRoofExchangePerTick = thin?.GetRawConstantValue() is float rate ? Fact.Known((double)rate) : default,
            WallExchangePerTick = Fact.Known((double)RoomTempTracker.WallEqualizeFactor * RoomTempTracker.FractionWallEqualizeCells * RoomTempTracker.EqualizationPowerOfFilledCells) };
    }
}
