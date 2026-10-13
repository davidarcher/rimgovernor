#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeDeepResources
    {
        private static readonly FieldInfo? Progress = BridgeCommon.PrivateInstanceField(typeof(CompScanner), "daysWorkingSinceLastFinding");
        private static readonly FieldInfo? Speed = BridgeCommon.PrivateInstanceField(typeof(CompScanner), "lastUserSpeed");
        private static readonly FieldInfo? LastScan = BridgeCommon.PrivateInstanceField(typeof(CompScanner), "lastScanTick");
        private static readonly FieldInfo? Target = BridgeCommon.PrivateInstanceField(typeof(CompLongRangeMineralScanner), "targetMineable");

        internal static Obs.DeepResourcesSection Read(Map map)
        {
            try {
                var facts = new Obs.DeepResourcesFacts();
                // Grid entries are discoveries, not unscanned underground potential.
                var seen = new HashSet<IntVec3>();
                foreach (var start in map.AllCells) {
                    var def = map.deepResourceGrid.ThingDefAt(start);
                    if (def == null || map.deepResourceGrid.CountAt(start) <= 0 || !seen.Add(start)) continue;
                    var cells = new List<IntVec3> { start };
                    long count = 0, x = 0, z = 0;
                    for (var i = 0; i < cells.Count; i++) {
                        var cell = cells[i];
                        count += map.deepResourceGrid.CountAt(cell); x += cell.x; z += cell.z;
                        foreach (var offset in GenAdj.AdjacentCells) {
                            var next = cell + offset;
                            if (next.InBounds(map) && map.deepResourceGrid.ThingDefAt(next) == def
                                && map.deepResourceGrid.CountAt(next) > 0 && seen.Add(next)) cells.Add(next);
                        }
                    }
                    var meanX = (double)x / cells.Count; var meanZ = (double)z / cells.Count;
                    var centre = cells.OrderBy(c => (c.x - meanX) * (c.x - meanX) + (c.z - meanZ) * (c.z - meanZ))
                        .ThenBy(c => c.x).ThenBy(c => c.z).First();
                    facts = facts with { Lumps = [.. facts.Lumps, new Obs.DeepResourceLump { DefName = def.defName, Count = count,
                        Centre = Cell(centre), CellCount = (uint)cells.Count }] };
                }
                foreach (var building in map.listerBuildings.allBuildingsColonist.OrderBy(b => b.thingIDNumber)) {
                    if (building.Spawned && building.TryGetComp<CompDeepDrill>() != null) {
                        var drill = new Obs.DeepDrillState { BuildingId = building.GetUniqueLoadID(), DefName = building.def.defName,
                            Position = Cell(building.Position), Powered = building.GetComp<CompPowerTrader>()?.PowerOn ?? false,
                            Designated = map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null };
                        // GetNextResource is the drill's own deposit lookup: false with no
                        // valuable deposit in radius, when vanilla drills stone chunks only.
                        if (DeepDrillUtility.GetNextResource(building.Position, map, out var resource, out var remaining, out _)
                            && resource != null && remaining > 0) {
                            drill = drill with { Resource = resource.defName }; drill = drill with { Remaining = remaining }; drill = drill with { Depleted = false };
                        } else drill = drill with { Depleted = true };
                        facts = facts with { Drills = [.. facts.Drills, drill] };
                    }
                    foreach (var scanner in building.AllComps.OfType<CompScanner>()) {
                        if (!(scanner is CompDeepScanner) && !(scanner is CompLongRangeMineralScanner)) continue;
                        var row = new Obs.MineralScannerState { BuildingId = building.GetUniqueLoadID(), DefName = building.def.defName,
                            Position = Cell(building.Position), Built = true, Powered = building.GetComp<CompPowerTrader>()?.PowerOn ?? false };
                        if (LastScan?.GetValue(scanner) is float last && !float.IsNaN(last) && !float.IsInfinity(last))
                            row = row with { Working = scanner.CanUseNow.Accepted && last >= 0 && last <= Find.TickManager.TicksGame
                                && last > Find.TickManager.TicksGame - 30 };
                        if (Progress?.GetValue(scanner) is float progress && Speed?.GetValue(scanner) is float speed
                            && progress >= 0 && !float.IsInfinity(progress) && speed > 0 && !float.IsInfinity(speed)
                            && scanner.Props.scanFindGuaranteedDays > 0) {
                            var ticks = Math.Ceiling(Math.Max(0, GameTime.Ticks(scanner.Props.scanFindGuaranteedDays - (double)progress) / speed));
                            if (!double.IsNaN(ticks) && !double.IsInfinity(ticks) && ticks < long.MaxValue) row = row with { TicksToNextFind = (long)ticks };
                        }
                        if (scanner is CompLongRangeMineralScanner) {
                            if (Target?.GetValue(scanner) is ThingDef target)
                                row = row with { TargetMineable = target.defName };
                            facts = facts with { LongRangeScanners = [.. facts.LongRangeScanners, row] };
                        } else facts = facts with { GroundScanners = [.. facts.GroundScanners, row] };
                    }
                }
                return new Obs.DeepResourcesSection { Outcome = facts };
            } catch (Exception) { return Unavailable(Common.UnavailableReason.ReadFailed); }
        }

        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static Obs.DeepResourcesSection Unavailable(Common.UnavailableReason reason) => new Obs.DeepResourcesSection {
            Outcome = new Common.Unavailable { Reason = reason, Detail = "Complete deep resource and scanner census unavailable." } };
    }
}
