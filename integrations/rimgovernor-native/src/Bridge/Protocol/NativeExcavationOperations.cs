#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Staged excavation read: certifies a requested rock cell set (a stage or a
    // whole target) for ordinary pawn mining. Roofed cells are admissible; the
    // site-level support answer is a counterfactual over all requested cells.
    internal static class NativeExcavationSite
    {
        internal const int MaxCells = 64;
        internal static bool Validate(Obs.ExcavationSiteRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity, 1..64 unique nonnegative cells and an access cell required.");
            if (request?.Scope?.ExpectedIdentity == null || request.Cells.Count < 1 || request.Cells.Count > MaxCells || !HasCell(request.AccessCell)) return false;
            var seen = new HashSet<(int, int)>();
            foreach (var cell in request.Cells) if (!HasCell(cell) || !seen.Add((cell.X, cell.Z))) return false;
            return true;
        }
        private static bool HasCell(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ && cell.X >= 0 && cell.Z >= 0;

        internal static Obs.ExcavationCell Row(IntVec3 cell, Map map, Common.ObservationContext context)
        {
            var row = new Obs.ExcavationCell { Cell = new Common.Cell { X = cell.x, Z = cell.z }, Fogged = false, MineDesignated = false, Eligible = false };
            if (!cell.InBounds(map)) { row.Eligible = false; row.Blocker = "Cell is outside the map"; return row; }
            row.Fogged = cell.Fogged(map);
            if (row.Fogged) { row.Eligible = false; row.Blocker = "Unknown excavation geometry"; return row; }
            var roof = cell.GetRoof(map);
            if (roof != null) row.RoofDefName = roof.defName;
            row.Walkable = cell.Walkable(map);
            var edifice = cell.GetEdifice(map);
            row.HoldsRoof = edifice != null && edifice.def.holdsRoof;
            row.MineDesignated = ExcavationTools.Designated(cell, map);
            var rock = ExcavationTools.RockAt(cell, map);
            if (rock != null)
            {
                row.MineableDefName = rock.def.defName; row.MineableId = rock.GetUniqueLoadID(); row.HitPoints = rock.HitPoints;
            }
            var blocker = ExcavationTools.CellBlocker(cell, map);
            row.Eligible = blocker == null && ExcavationTools.Eligible(cell, map);
            if (blocker != null) row.Blocker = blocker;
            else if (!row.Eligible) row.Blocker = "Native mining designation is not accepted at this cell";
            return row;
        }

        internal static Obs.ExcavationSiteSnapshot Read(Map map, Obs.ExcavationSiteRequest request, Common.ObservationContext context)
        {
            var cells = request.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToList();
            var access = new IntVec3(request.AccessCell.X, 0, request.AccessCell.Z);
            var snapshot = new Obs.ExcavationSiteSnapshot { Context = context, CollapsePending = map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0 };
            foreach (var cell in cells) snapshot.Cells.Add(Row(cell, map, context));
            var known = cells.Where(c => c.InBounds(map) && !c.Fogged(map)).ToList();
            if (known.Count == 0) { snapshot.SupportAfterRemoval = Obs.ExcavationSupport.Unknown; snapshot.SupportBlocker = "Unknown excavation geometry"; }
            else
            {
                var support = ExcavationSafety.Check(map, known, out var checkedRoofs, out var blocker);
                snapshot.RoofCellsChecked = (uint)checkedRoofs;
                snapshot.SupportAfterRemoval = support == ExcavationSafety.Support.Supported ? Obs.ExcavationSupport.Supported
                    : support == ExcavationSafety.Support.Unknown ? Obs.ExcavationSupport.Unknown : Obs.ExcavationSupport.Unsupported;
                if (blocker != null) snapshot.SupportBlocker = blocker;
                if (known.Count < cells.Count && snapshot.SupportAfterRemoval == Obs.ExcavationSupport.Supported)
                { snapshot.SupportAfterRemoval = Obs.ExcavationSupport.Unknown; snapshot.SupportBlocker = "Fogged cells in the requested set are unknown"; }
            }
            if (snapshot.CollapsePending)
            { snapshot.SupportAfterRemoval = Obs.ExcavationSupport.Unsupported; snapshot.SupportBlocker = "Roof collapse is already pending"; }
            // The access cell may itself be rock (single-cell dispatch reads pass
            // the target): a miner stands on any visible walkable neighbour.
            var stand = ExcavationTools.StandingCell(access, map);
            // A standing cell nobody can path to (the mouth walled shut, the
            // pocket beyond it enclosed) is not reachable access; only mobile
            // colonists can say so, an all-downed colony leaves it standing.
            var mobile = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).ToList();
            snapshot.AccessReachable = stand.IsValid && (mobile.Count == 0 || mobile.Any(p => p.CanReach(stand, PathEndMode.OnCell, Danger.None)));
            var workers = snapshot.AccessReachable ? ExcavationTools.Workers(map, stand) : new List<Pawn>();
            snapshot.WorkerAvailable = workers.Count > 0;
            foreach (var worker in workers) snapshot.WorkerIds.Add(worker.GetUniqueLoadID());
            return snapshot;
        }
    }

    // ExcavateIntent designates one visible rock cell for ordinary pawn
    // mining. A standing Mine designation is adopted; a cell the pawns already
    // cleared applies as cleared. Applied means designated, not mined: the
    // site read decides progress.
    internal static class NativeExcavationOperations
    {
        private static string? Refusal(Operations.ExcavateIntent? intent, Common.ObservationContext context, out Map? map, out IntVec3 cell, out Mineable? rock)
        {
            map = null; cell = IntVec3.Invalid; rock = null;
            if (intent?.Cell == null || !intent.Cell.HasX || !intent.Cell.HasZ || intent.Cell.X < 0 || intent.Cell.Z < 0
                || !intent.HasExpectedMineableDefName || !ProtoBoundary.IsIdentifier(intent.ExpectedMineableDefName)) return "Excavation requires a cell and the expected rock definition.";
            map = ProtoBoundary.ResolveMap(context);
            if (map == null) return "Current map required.";
            if (map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0) return "Roof collapse is pending on this map.";
            cell = new IntVec3(intent.Cell.X, 0, intent.Cell.Z);
            rock = ExcavationTools.RockAt(cell, map);
            if (rock == null && cell.InBounds(map) && !cell.Fogged(map) && cell.Walkable(map)) return null;
            if (rock == null || cell.Fogged(map) || rock.def.defName != intent.ExpectedMineableDefName) { rock = null; return "Expected rock is not visible at the cell."; }
            var blocker = ExcavationTools.CellBlocker(cell, map);
            if (blocker == null && ExcavationSafety.Check(map, new[] { cell }, out _, out var support) != ExcavationSafety.Support.Supported) blocker = support;
            if (blocker == null && !ExcavationTools.Designated(cell, map) && !new Designator_Mine().CanDesignateCell(cell).Accepted) blocker = "Native mining designation is not accepted at this cell";
            return blocker;
        }
        internal static Common.Failure? Validate(Operations.ExcavateIntent? intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out _, out _, out _);
            return refusal == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refusal);
        }
        internal static Receipts.EffectEvidence Apply(Operations.ExcavateIntent intent, Common.ObservationContext context)
        {
            var refusal = Refusal(intent, context, out var map, out var cell, out var rock);
            if (refusal != null) throw new InvalidOperationException("Excavation prerequisites changed before apply: " + refusal);
            var adopted = rock != null && ExcavationTools.Designated(cell, map!);
            if (rock != null)
            {
                if (MiningGuard.OpenExcavation(map!, cell) == null)
                    MiningGuard.State().Excavations.Add(new ExcavationRecord { MapId = map!.uniqueID, X = cell.x, Z = cell.z, Definition = rock.def.defName, Started = Find.TickManager.TicksGame });
                if (!adopted) new Designator_Mine().DesignateSingleCell(cell);
            }
            var now = ExcavationTools.RockAt(cell, map!);
            var effect = new Receipts.ExcavationEffect { Cell = new Common.Cell { X = cell.x, Z = cell.z }, MineableDefName = intent.ExpectedMineableDefName,
                AdoptedExistingDesignation = adopted, Cleared = now == null, Designated = now != null && ExcavationTools.Designated(cell, map!) };
            effect.Cancelled = now != null && !effect.Designated;
            if (!effect.Cleared && !effect.Designated) throw new InvalidOperationException("Excavation designation was not observed.");
            return new Receipts.EffectEvidence { Excavation = effect };
        }
    }

    internal sealed class ExcavateActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeExcavationOperations.Validate(action.Excavate, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeExcavationOperations.Apply(action.Excavate, context);
    }
}
