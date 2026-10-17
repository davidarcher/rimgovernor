#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

public sealed partial class PawnControlFixture
{
    private static CellRect thermalInterior;
    private static IntVec3[] thermalMissing = [];
    private static Building_Heater? thermalHeater;
    private static Building_Bed? thermalBed;
    private static Pawn? thermalSleeper;
    private static float thermalBaseline, thermalPeak;
    private static int thermalStart, thermalRoofTick;
    private static bool thermalActive, thermalPatched, thermalSlept;
    private static readonly HashSet<string> thermalMethods = new(StringComparer.Ordinal);

    [Tool("test/portfolio_thermal_prepare", Description="UNSAFE FOR MODEL EXECUTION. Prepare the normal planner's own small room geometry with a leaky roof, powered heater and tired occupant. Installs no selected task or Method.")]
    public async Task<object> PortfolioThermalPrepare(IRimBridgeContext ctx, CancellationToken ct,
        int x, int z, int width, int height, int doorX, int doorZ)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            thermalActive = false;
            var map = Find.CurrentMap;
            if (!Find.TickManager.Paused || width < 2 || height < 2 || width * height < 6 || width > 5 || height > 5)
                throw new InvalidOperationException("A paused planned rectangular room with sides 2..5 and at least 6 cells is required.");
            thermalInterior = new CellRect(x, z, width, height);
            var bedCell = new IntVec3(x + width - 1, 0, z + height - 2);
            var heaterCell = new IntVec3(x, 0, z + height - 1);
            var bedFootprint = GenAdj.OccupiedRect(bedCell, Rot4.North, ThingDefOf.Bed.size);
            var heaterFootprint = GenAdj.OccupiedRect(heaterCell, Rot4.North, ThingDef.Named("Heater").size);
            if (!bedFootprint.Cells.All(thermalInterior.Contains) || !heaterFootprint.Cells.All(thermalInterior.Contains)
                || bedFootprint.Cells.Any(heaterFootprint.Contains))
                throw new InvalidOperationException("Native furniture footprints do not fit the unchanged planned interior.");
            var shell = thermalInterior.ExpandedBy(1);
            if (!shell.Cells.All(c => c.InBounds(map))) throw new InvalidOperationException("Planned room is outside map.");
            var people = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
            thermalSleeper = people.First();
            foreach (var pawn in people)
            {
                pawn.jobs.StopAll();
                pawn.Position = new IntVec3(x + width + 5, 0, z + 3);
                pawn.Notify_Teleported(true, true);
                foreach (var type in DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => !pawn.WorkTypeIsDisabled(t)))
                    pawn.workSettings.SetPriority(type, 0);
            }
            foreach (var thing in shell.Cells.SelectMany(c => c.GetThingList(map)).Distinct().Where(t => t is not Pawn).ToArray()) thing.Destroy();
            Thing Spawn(string name, IntVec3 cell)
            {
                var def = ThingDef.Named(name);
                var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                thing.SetFaction(Faction.OfPlayer);
                return GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
            }
            var door = new IntVec3(doorX, 0, doorZ);
            if (!shell.Contains(door) || thermalInterior.Contains(door)) throw new InvalidOperationException("Planned door is not on shell.");
            foreach (var cell in shell.Cells)
            {
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Concrete);
                map.areaManager.Home[cell] = true;
                if (!thermalInterior.Contains(cell)) Spawn(cell == door ? "Door" : "Wall", cell);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                map.areaManager.BuildRoof[cell] = false;
            }
            // More than one quarter open roof: the native baseline stays exposed despite the powered heater.
            thermalMissing = thermalInterior.Cells.OrderBy(c => c.z).ThenBy(c => c.x)
                .Take(thermalInterior.Area / 4 + 1).ToArray();
            foreach (var cell in thermalMissing) map.roofGrid.SetRoof(cell, null);
            thermalHeater = (Building_Heater)Spawn("Heater", heaterCell);
            thermalHeater.TryGetComp<CompTempControl>().targetTemperature = 21;
            thermalBed = (Building_Bed)Spawn("Bed", bedCell);
            var generator = Spawn("WoodFiredGenerator", new IntVec3(x - 4, 0, z + height - 1));
            var fuel = generator.TryGetComp<CompRefuelable>(); fuel.Refuel(fuel.Props.fuelCapacity);
            for (var at = x - 4; at <= x; at++) Spawn("PowerConduit", new IntVec3(at, 0, z + height - 1));
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            thermalBed.GetRoom().Temperature = -10;
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            // Bounded physics-only baseline; no roof designation exists and no control Method is installed.
            for (var i = 0; i < 600; i++) Find.TickManager.DoSingleTick();
            thermalBaseline = thermalBed.Position.GetTemperature(map);
            if (!thermalHeater.TryGetComp<CompPowerTrader>().PowerOn || thermalBed.GetRoom().OpenRoofCount == 0 || thermalBaseline > -8)
                throw new InvalidOperationException("Prepared leaky powered-heater baseline unavailable.");
            foreach (var cell in thermalMissing) map.areaManager.BuildRoof[cell] = true;
            foreach (var pawn in people)
            {
                pawn.jobs.StopAll();
                pawn.needs.food.CurLevelPercentage = .95f;
                pawn.needs.rest.CurLevelPercentage = .95f;
                pawn.needs.joy.CurLevelPercentage = .95f;
            }
            thermalSleeper.Position = new IntVec3(x + 1, 0, z + height - 2);
            thermalSleeper.Notify_Teleported(true, true);
            thermalStart = Find.TickManager.TicksGame; thermalRoofTick = -1;
            thermalPeak = thermalBaseline; thermalSlept = false; thermalMethods.Clear();
            if (!thermalPatched)
            {
                var harmony = new Harmony("rimgovernor.fixture.portfolio-thermal");
                harmony.Patch(AccessTools.Method(typeof(NativePawnActivities), "Apply"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(ThermalApplied)));
                harmony.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), postfix: new HarmonyMethod(typeof(PawnControlFixture), nameof(ThermalTick)));
                thermalPatched = true;
            }
            thermalActive = true;
            return ThermalStatus();
        }, ct).ConfigureAwait(false);

    private static void ThermalApplied(PawnActivities batch)
    {
        if (!thermalActive) return;
        foreach (var order in batch.Orders.Where(o => !o.Release && o.Method.StartsWith("coupled-", StringComparison.Ordinal)
            && o.Steps.Any(s => s.Job == "BuildRoof" && s.Destination is { } c && thermalMissing.Contains(new IntVec3(c.X, 0, c.Z)))))
            thermalMethods.Add(order.Method);
    }
    private static void ThermalTick(Pawn ___pawn)
    {
        if (!thermalActive || ___pawn != thermalSleeper) return;
        var map = ___pawn.Map;
        if (thermalMissing.All(c => c.Roofed(map)))
        {
            if (thermalRoofTick < 0) thermalRoofTick = Find.TickManager.TicksGame;
            var execution = NativePawnActivities.Read(___pawn);
            if (execution?.Method.StartsWith("coupled-", StringComparison.Ordinal) == true && ___pawn.CurJobDef == JobDefOf.LayDown
                && !___pawn.Awake() && ___pawn.CurrentBed() == thermalBed)
            {
                thermalSlept = true;
                thermalPeak = Math.Max(thermalPeak, ___pawn.Position.GetTemperature(map));
            }
        }
    }
    private static object ThermalStatus() => new
    {
        success = true, tick = Find.TickManager.TicksGame, elapsed = Find.TickManager.TicksGame - thermalStart,
        baseline = thermalBaseline, temperature = thermalBed!.Position.GetTemperature(thermalBed.Map), occupiedPeak = thermalPeak,
        roofComplete = thermalMissing.All(c => c.Roofed(thermalBed.Map)), openRoof = thermalBed.GetRoom().OpenRoofCount,
        slept = thermalSlept, roofTick = thermalRoofTick, methods = thermalMethods.Order(StringComparer.Ordinal).ToArray(),
        powered = thermalHeater!.TryGetComp<CompPowerTrader>().PowerOn
    };
    [Tool("test/portfolio_thermal_read", Description="UNSAFE FOR MODEL EXECUTION. Observe native roof completion and occupied room warming after normal planner work; writes nothing.")]
    public async Task<object> PortfolioThermalRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(ThermalStatus, ct).ConfigureAwait(false);
}
