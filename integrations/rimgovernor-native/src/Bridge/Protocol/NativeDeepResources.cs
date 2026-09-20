#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    internal static class NativeDeepResources
    {
        private const int Limit = 256;
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
                    if (facts.Lumps.Count == Limit) return Unavailable(Common.UnavailableReason.LimitExceeded);
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
                    facts.Lumps.Add(new Obs.DeepResourceLump { DefName = def.defName, Count = count,
                        Centre = Cell(centre), CellCount = (uint)cells.Count });
                }
                NativeDrillOwnership.Prune(map);
                foreach (var building in map.listerBuildings.allBuildingsColonist.OrderBy(b => b.thingIDNumber)) {
                    if (building.Spawned && building.TryGetComp<CompDeepDrill>() != null) {
                        if (facts.GroundScanners.Count + facts.LongRangeScanners.Count + facts.Drills.Count == Limit)
                            return Unavailable(Common.UnavailableReason.LimitExceeded);
                        var drill = new Obs.DeepDrillState { BuildingId = building.GetUniqueLoadID(), DefName = building.def.defName,
                            Position = Cell(building.Position), Powered = building.GetComp<CompPowerTrader>()?.PowerOn ?? false,
                            ControllerOwned = NativeDrillOwnership.Owned(building),
                            Designated = map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null };
                        // GetNextResource is the drill's own deposit lookup: false with no
                        // valuable deposit in radius, when vanilla drills stone chunks only.
                        if (DeepDrillUtility.GetNextResource(building.Position, map, out var resource, out var remaining, out _)
                            && resource != null && remaining > 0) {
                            drill.Resource = resource.defName; drill.Remaining = remaining; drill.Depleted = false;
                        } else drill.Depleted = true;
                        facts.Drills.Add(drill);
                    }
                    foreach (var scanner in building.AllComps.OfType<CompScanner>()) {
                        if (!(scanner is CompDeepScanner) && !(scanner is CompLongRangeMineralScanner)) continue;
                        if (facts.GroundScanners.Count + facts.LongRangeScanners.Count == Limit)
                            return Unavailable(Common.UnavailableReason.LimitExceeded);
                        var row = new Obs.MineralScannerState { BuildingId = building.GetUniqueLoadID(), DefName = building.def.defName,
                            Position = Cell(building.Position), Built = true, Powered = building.GetComp<CompPowerTrader>()?.PowerOn ?? false };
                        if (LastScan?.GetValue(scanner) is float last && !float.IsNaN(last) && !float.IsInfinity(last))
                            row.Working = scanner.CanUseNow.Accepted && last >= 0 && last <= Find.TickManager.TicksGame
                                && last > Find.TickManager.TicksGame - 30;
                        if (Progress?.GetValue(scanner) is float progress && Speed?.GetValue(scanner) is float speed
                            && progress >= 0 && !float.IsInfinity(progress) && speed > 0 && !float.IsInfinity(speed)
                            && scanner.Props.scanFindGuaranteedDays > 0) {
                            var ticks = Math.Ceiling(Math.Max(0, (scanner.Props.scanFindGuaranteedDays - (double)progress) * 60000 / speed));
                            if (!double.IsNaN(ticks) && !double.IsInfinity(ticks) && ticks < long.MaxValue) row.TicksToNextFind = (long)ticks;
                        }
                        if (scanner is CompLongRangeMineralScanner) {
                            if (Target?.GetValue(scanner) is ThingDef target && target.building?.mineableThing != null)
                                row.TargetResource = target.building.mineableThing.defName;
                            facts.LongRangeScanners.Add(row);
                        } else facts.GroundScanners.Add(row);
                    }
                }
                return new Obs.DeepResourcesSection { Observed = facts };
            } catch (Exception) { return Unavailable(Common.UnavailableReason.ReadFailed); }
        }

        private static Common.Cell Cell(IntVec3 c) => new Common.Cell { X = c.x, Z = c.z };
        private static Obs.DeepResourcesSection Unavailable(Common.UnavailableReason reason) => new Obs.DeepResourcesSection {
            Unavailable = new Common.Unavailable { Reason = reason, Detail = "Complete deep resource and scanner census unavailable." } };
    }
}
