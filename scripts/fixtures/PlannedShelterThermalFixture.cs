#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace HomeBridge.BridgeTools;

// Private disposable acceptance only (#3323): an open, unroofed patch where a planned 3x3 shelter stands with nothing
// placed at all, beside a fuelled wood generator and a conduit run. The planned thermal read must sample the room's
// interior and ring cells and return a planned Heater's native facts at its planned cell, joined to that network.
// Nothing is placed in the room or ordered.
public sealed partial class PawnControlFixture
{
    private static IntVec3 shelterOrigin;
    private static Thing? shelterGenerator;

    // Patch: generator at (1,3), conduits (3..5,3), the planned room's ring x 6..10, z 1..5 (interior 7..9, 2..4).
    private const int ShelterPatchWidth = 12, ShelterPatchHeight = 7;

    private static IntVec3 ShelterAt(int x, int z) => new(shelterOrigin.x + x, 0, shelterOrigin.z + z);

    [Tool("test/planned_shelter_thermal_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Clear and unroof one open 12x7 patch near a builder; spawn a fuelled wood generator and a conduit run beside an empty planned 3x3 room.")]
    public async Task<object> PlannedShelterThermalPrepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var builder = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                .FirstOrDefault(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                ?? throw new InvalidOperationException("No colonist able to construct.");
            shelterOrigin = GenRadial.RadialCellsAround(builder.Position, 40, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, ShelterPatchWidth, ShelterPatchHeight).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.GetTerrain(map).IsWater
                    && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.Roofed(map)
                    && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                && builder.CanReach(c, PathEndMode.Touch, Danger.None));
            if (shelterOrigin == default) throw new InvalidOperationException("No open, unroofed, reachable 12x7 patch.");
            foreach (var cell in new CellRect(shelterOrigin.x, shelterOrigin.z, ShelterPatchWidth, ShelterPatchHeight).Cells)
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category is ThingCategory.Item or ThingCategory.Filth).ToList()) thing.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                map.areaManager.BuildRoof[cell] = false;
            }
            Thing Spawn(string name, IntVec3 cell)
            {
                var thing = ThingMaker.MakeThing(ThingDef.Named(name));
                thing.SetFaction(Faction.OfPlayer);
                return GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
            }
            shelterGenerator = Spawn("WoodFiredGenerator", ShelterAt(1, 3));
            var fuel = shelterGenerator.TryGetComp<CompRefuelable>(); fuel.Refuel(fuel.Props.fuelCapacity);
            for (var x = 3; x <= 5; x++) Spawn("PowerConduit", ShelterAt(x, 3));
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            return new { success = true, origin = new { x = shelterOrigin.x, z = shelterOrigin.z }, generator = shelterGenerator.GetUniqueLoadID() };
        }, ct).ConfigureAwait(false);

    [Tool("test/planned_shelter_thermal_read", Description = "UNSAFE FOR MODEL EXECUTION. Read the prepared empty planned room's cells and a planned Heater (and a planned bed, which is no heat source) through the construction activity read; check them against native mechanics.")]
    public async Task<object> PlannedShelterThermalRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (shelterOrigin == default || shelterGenerator == null) throw new InvalidOperationException("Prepare planned_shelter_thermal first.");
            var map = Find.CurrentMap;
            var identity = Context().Identity!;
            Cell C(IntVec3 cell) => new(cell.x, cell.z);
            var interior = new CellRect(ShelterAt(7, 2).x, ShelterAt(7, 2).z, 3, 3);
            var ring = interior.ExpandedBy(1);
            if (ring.Cells.Any(c => c.GetThingList(map).Any(t => t is IConstructible || t.def.category == ThingCategory.Building)))
                throw new InvalidOperationException("The planned room is not empty: something is placed in its cells.");
            var heaterCell = ShelterAt(8, 3); var bedCell = ShelterAt(7, 2);
            var heaterSite = "planned/room1/build/Heater/" + heaterCell.x + "/" + heaterCell.z;
            var read = NativeConstructionActivity.Read(new(identity, default)
            {
                Thermal = new()
                {
                    Rooms = [new("1", C(interior.Min), interior.Width, interior.Height)],
                    Heaters = [new(heaterSite, "Heater", C(heaterCell)), new("planned/room1/build/Bed/" + bedCell.x + "/" + bedCell.z, "Bed", C(bedCell))],
                },
            });
            var thermal = read.Thermal ?? throw new InvalidOperationException("The construction activity read answered no planned thermal read; findings " + string.Join(",", read.Findings));
            if (thermal.Truncated) throw new InvalidOperationException("The planned thermal read is truncated for one 3x3 room.");
            var rows = thermal.Cells.ToDictionary(c => c.Cell);
            foreach (var cell in ring.Cells)
            {
                if (!rows.TryGetValue(C(cell), out var row)) throw new InvalidOperationException("Planned room cell " + cell + " has no thermal row.");
                var temperature = cell.GetTemperature(map);
                if (!row.Temperature.IsKnown || Math.Abs(row.Temperature.Value - temperature) > 1e-3)
                    throw new InvalidOperationException("Cell " + cell + " temperature " + row.Temperature + " disagrees with native " + temperature + ".");
                if (row.Roof != "none" || row.Solid || row.Door || row.Site.Length != 0 || row.FutureDefinition.Length != 0)
                    throw new InvalidOperationException("Cell " + cell + " of the empty, unroofed room reads something placed: " + row);
            }
            if (rows.Count != ring.Area) throw new InvalidOperationException("The read sampled " + rows.Count + " cells, not the room's " + ring.Area + ".");
            var source = thermal.Sources.SingleOrDefault() ?? throw new InvalidOperationException("Expected one planned heater source, got " + thermal.Sources.Count + ".");
            var heater = ThingDef.Named("Heater");
            var control = heater.GetCompProperties<CompProperties_TempControl>();
            var watts = heater.GetCompProperties<CompProperties_Power>().PowerConsumption;
            var net = PowerConnectionMaker.BestTransmitterForConnector(heaterCell, map)?.PowerNet
                ?? throw new InvalidOperationException("The planned heater cell joins no network beside the conduit run.");
            var surplus = net.powerComps.Where(p => p.PowerOn).Sum(p => p.PowerOutput);
            if (source.Target != heaterSite || source.Cell != C(heaterCell) || source.Complete)
                throw new InvalidOperationException("Planned heater source is not the planned, incomplete site: " + source);
            if (!source.HeatPerTick.IsKnown || Math.Abs(source.HeatPerTick.Value - control.energyPerSecond / 60.0) > 1e-6
                || !source.TargetTemperature.IsKnown || Math.Abs(source.TargetTemperature.Value - control.defaultTargetTemperature) > 1e-6)
                throw new InvalidOperationException("Planned heater heat " + source.HeatPerTick + " / target " + source.TargetTemperature + " disagree with the def's " + control.energyPerSecond / 60.0 + " / " + control.defaultTargetTemperature + ".");
            if (!source.PowerNetwork.Split('/').Contains(shelterGenerator.GetUniqueLoadID())
                || !source.AdditionalWatts.IsKnown || Math.Abs(source.AdditionalWatts.Value - watts) > 1e-3
                || !source.SurplusWatts.IsKnown || Math.Abs(source.SurplusWatts.Value - surplus) > 1e-3
                || source.SupplyTicks.IsKnown != surplus >= watts)
                throw new InvalidOperationException("Planned heater supply disagrees with the network a connector at its cell joins (surplus " + surplus + ", draw " + watts + "): " + source);
            if (!thermal.Outdoors.IsKnown || !thermal.ThinRoofExchangePerTick.IsKnown || !thermal.WallExchangePerTick.IsKnown || thermal.Workers.Count == 0)
                throw new InvalidOperationException("The planned thermal read lacks the map's exchange constants or builders.");
            return new { success = true, cells = rows.Count, heater = new { source.Target, heat = source.HeatPerTick.Value, target = source.TargetTemperature.Value,
                network = source.PowerNetwork, additional = source.AdditionalWatts.Value, surplus = source.SurplusWatts.Value, supplied = source.SupplyTicks.IsKnown },
                outdoors = thermal.Outdoors.Value, workers = thermal.Workers.Count };
        }, ct).ConfigureAwait(false);
}
