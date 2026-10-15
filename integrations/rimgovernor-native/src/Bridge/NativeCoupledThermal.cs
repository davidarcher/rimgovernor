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
    internal static Obs.CoupledThermalRead Read(Map map, Thing[] sites)
    {
        var heaters = map.listerThings.AllThings.Where(t => t is Building_Heater
            || t is IConstructible && t.def.entityDefToBuild is ThingDef d && d.thingClass == typeof(Building_Heater))
            .OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).Take(17).ToArray();
        var cells = new SortedSet<(int X, int Z)>(); var truncated = heaters.Length > 16;
        foreach (var origin in sites.Concat(heaters.Take(16)).Select(t => t.Position).Distinct())
        for (var x = origin.x - 6; x <= origin.x + 6; x++)
        for (var z = origin.z - 6; z <= origin.z + 6; z++)
        {
            if (!new IntVec3(x, 0, z).InBounds(map)) continue;
            if (cells.Count == 1024 && !cells.Contains((x,z))) { truncated = true; continue; }
            cells.Add((x,z));
        }
        var rows = cells.Select(c =>
        {
            var cell = new IntVec3(c.X, 0, c.Z); var edifice = cell.GetEdifice(map);
            var site = cell.GetThingList(map).FirstOrDefault(t => t is IConstructible);
            var future = site?.def.entityDefToBuild as ThingDef;
            var door = edifice as Building_Door; var doorDef = door?.def ?? (future?.IsDoor == true ? future : null);
            var roof = cell.GetRoof(map);
            return new Obs.ThermalCell(new(c.X, c.Z), Fact.Known((double)cell.GetTemperature(map)),
                roof == null ? "none" : roof.isThickRoof ? "thick" : "thin",
                edifice?.def.Fillage == FillCategory.Full, door != null, door != null && !door.Open,
                site?.GetUniqueLoadID() ?? "", future?.defName ?? "", future?.Fillage == FillCategory.Full,
                future?.IsDoor == true, doorDef == null ? 0 : doorDef.building.doorTempEqualizeRate / (double)doorDef.building.doorTempEqualizeIntervalClosed,
                map.areaManager.BuildRoof[cell]);
        }).ToArray();
        var sources = heaters.Take(16).Select(t =>
        {
            var definition = t is IConstructible ? (ThingDef)t.def.entityDefToBuild : t.def;
            var temperature = definition.GetCompProperties<CompProperties_TempControl>();
            var power = t.TryGetComp<CompPowerTrader>();
            var transmitter = PowerConnectionMaker.BestTransmitterForConnector(t.Position, map);
            var net = power?.PowerNet ?? transmitter?.PowerNet;
            var watts = definition.GetCompProperties<CompProperties_Power>()?.PowerConsumption ?? 0;
            // No future weather/fuel claim: supply is assessed only from the current network's observed surplus.
            var surplus = net == null ? 0 : net.powerComps.Where(p => p.PowerOn).Sum(p => p.PowerOutput);
            var ready = t is Building_Heater;
            var additional = Math.Max(0, watts + (ready ? power?.PowerOutput ?? 0 : 0));
            Fact<double> supply = net != null && (ready ? power?.PowerOn == true && surplus >= additional : surplus >= additional)
                ? Fact.Known((double)RimGovernor.Brain.Domain.GameTime.TicksPerDay) : default;
            return new Obs.ThermalSource(t.GetUniqueLoadID(), new(t.Position.x, t.Position.z), ready,
                temperature == null ? default : Fact.Known(temperature.energyPerSecond / 60.0),
                Fact.Known((double)(t.TryGetComp<CompTempControl>()?.targetTemperature ?? temperature?.defaultTargetTemperature ?? 20)), supply)
                { PowerNetwork = net == null ? "" : string.Join("/", net.powerComps.Select(p => p.parent.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal)),
                    AdditionalWatts = Fact.Known((double)additional), SurplusWatts = net == null ? default : Fact.Known((double)surplus) };
        }).ToArray();
        var workers = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Take(16)
            .Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)).Select(p => new Obs.ThermalWorker(p.GetUniqueLoadID(),
                p.GetStatValue(StatDefOf.ConstructionSpeed) > 0
                    ? Fact.Known(65.0 / (p.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7)) : default)).ToArray();
                var roofRoutes = new List<Obs.ThermalRoofRoute>();
        foreach (var pawn in map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Take(16))
        foreach (var cell in rows.Where(c => c.BuildRoof && c.Roof == "none").Take(32))
        {
            if (roofRoutes.Count == 128) { truncated = true; break; }
            using var route = map.pathFinder.FindPathNow(pawn.Position, new IntVec3(cell.Cell.X, 0, cell.Cell.Z),
                TraverseParms.For(pawn, Danger.None), peMode: PathEndMode.OnCell);
            roofRoutes.Add(new(pawn.GetUniqueLoadID(), cell.Cell, route.Found ? Fact.Known((double)route.TotalCost) : default));
        }
        // Constants are read from this game's mechanics; unsupported versions produce unknown observations.
        var thin = typeof(RoomTempTracker).GetField("ThinRoofEqualizeRate", BindingFlags.Static | BindingFlags.NonPublic);
        return new() { Cells = EquatableArray.From(rows), Sources = EquatableArray.From(sources), Workers = EquatableArray.From(workers),
            RoofRoutes = EquatableArray.From(roofRoutes), Outdoors = Fact.Known((double)map.mapTemperature.OutdoorTemp),
            ThinRoofExchangePerTick = thin?.GetRawConstantValue() is float rate ? Fact.Known((double)rate) : default,
            WallExchangePerTick = Fact.Known((double)RoomTempTracker.WallEqualizeFactor * RoomTempTracker.FractionWallEqualizeCells * RoomTempTracker.EqualizationPowerOfFilledCells),
            Truncated = truncated };
    }
}
