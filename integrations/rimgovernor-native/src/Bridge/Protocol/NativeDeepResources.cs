using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Native
{
    internal static class NativeDeepResources
    {
        private static readonly FieldInfo? Progress = BridgeCommon.PrivateInstanceField(typeof(CompScanner), "daysWorkingSinceLastFinding");
        private static readonly FieldInfo? Speed = BridgeCommon.PrivateInstanceField(typeof(CompScanner), "lastUserSpeed");
        private static readonly FieldInfo? LastScan = BridgeCommon.PrivateInstanceField(typeof(CompScanner), "lastScanTick");

        // The map's discovered lumps, its player scanners and drills; throws on anything it cannot read.
        internal static RimGovernor.Brain.GameFacts.Observations.DeepResources Read(Map map)
        {
            var lumps = new List<RimGovernor.Brain.GameFacts.Observations.DeepResourceLump>();
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
                lumps.Add(new RimGovernor.Brain.GameFacts.Observations.DeepResourceLump { Definition = def.defName, Count = count, Centre = new Cell(centre.x, centre.z), CellCount = cells.Count });
            }
            var drills = new List<RimGovernor.Brain.GameFacts.Observations.DeepDrill>();
            var ground = new List<RimGovernor.Brain.GameFacts.Observations.MineralScanner>();
            var longRange = new List<RimGovernor.Brain.GameFacts.Observations.MineralScanner>();
            foreach (var building in map.listerBuildings.allBuildingsColonist.OrderBy(b => b.thingIDNumber)) {
                if (building.Spawned && building.TryGetComp<CompDeepDrill>() != null)
                    drills.Add(new RimGovernor.Brain.GameFacts.Observations.DeepDrill {
                        Id = building.GetUniqueLoadID(),
                        Definition = building.def.defName,
                        Position = new Cell(building.Position.x, building.Position.z),
                        // GetNextResource is the drill's own deposit lookup: false with no
                        // valuable deposit in radius, when vanilla drills stone chunks only.
                        Deposit = DeepDrillUtility.GetNextResource(building.Position, map, out var resource, out var remaining, out _) && resource != null && remaining > 0
                            ? new RimGovernor.Brain.GameFacts.Observations.DrillDeposit { Resource = resource.defName, Remaining = remaining } : null,
                        Designated = map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null,
                    });
                foreach (var scanner in building.AllComps.OfType<CompScanner>()) {
                    if (!(scanner is CompDeepScanner) && !(scanner is CompLongRangeMineralScanner)) continue;
                    var row = new RimGovernor.Brain.GameFacts.Observations.MineralScanner {
                        Definition = building.def.defName,
                        Position = new Cell(building.Position.x, building.Position.z),
                        Powered = building.GetComp<CompPowerTrader>()?.PowerOn ?? false,
                        Working = Working(scanner),
                        TicksToNextFind = TicksToNextFind(scanner),
                    };
                    (scanner is CompLongRangeMineralScanner ? longRange : ground).Add(row);
                }
            }
            return new RimGovernor.Brain.GameFacts.Observations.DeepResources { Lumps = [.. lumps], GroundScanners = [.. ground], LongRangeScanners = [.. longRange], Drills = [.. drills] };
        }

        // Usable and used within the last 30 ticks (lastScanTick starts at -1, never used).
        private static bool Working(CompScanner scanner)
        {
            if (!(LastScan?.GetValue(scanner) is float last) || float.IsNaN(last) || float.IsInfinity(last))
                throw new InvalidOperationException("CompScanner.lastScanTick is unreadable.");
            return scanner.CanUseNow.Accepted && last >= 0 && last <= Find.TickManager.TicksGame && last > Find.TickManager.TicksGame - 30;
        }

        // The remaining work to the guaranteed find at the last user's speed (1 before any use).
        private static long TicksToNextFind(CompScanner scanner)
        {
            if (!(Progress?.GetValue(scanner) is float progress) || !(Speed?.GetValue(scanner) is float speed)
                || progress < 0 || float.IsNaN(progress) || float.IsInfinity(progress) || !(speed > 0) || float.IsInfinity(speed)
                || !(scanner.Props.scanFindGuaranteedDays > 0))
                throw new InvalidOperationException("CompScanner progress, speed or guaranteed find days are unreadable.");
            var ticks = Math.Ceiling(Math.Max(0, GameTime.Ticks(scanner.Props.scanFindGuaranteedDays - (double)progress) / speed));
            if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks >= long.MaxValue)
                throw new InvalidOperationException("CompScanner time to next find overflows.");
            return (long)ticks;
        }
    }
}