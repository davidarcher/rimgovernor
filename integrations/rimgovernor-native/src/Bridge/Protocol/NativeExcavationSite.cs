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
        internal static bool Validate(Obs.ExcavationSiteRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity, 1 or more unique nonnegative cells and an access cell required.");
            if (request?.Scope?.ExpectedIdentity == null || request.Cells.Count < 1 || !HasCell(request.AccessCell)) return false;
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
            var known = cells.Where(c => c.InBounds(map)).ToList();
            if (known.Count == 0) { snapshot.SupportAfterRemoval = Obs.ExcavationSupport.Unknown; snapshot.SupportBlocker = "Unknown excavation geometry"; }
            else
            {
                var support = ExcavationSafety.Check(map, known, out var checkedRoofs, out var blocker, throughFog: true);
                snapshot.RoofCellsChecked = (uint)checkedRoofs;
                snapshot.SupportAfterRemoval = support == ExcavationSafety.Support.Supported ? Obs.ExcavationSupport.Supported
                    : support == ExcavationSafety.Support.Unknown ? Obs.ExcavationSupport.Unknown : Obs.ExcavationSupport.Unsupported;
                if (blocker != null) snapshot.SupportBlocker = blocker;
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
}
