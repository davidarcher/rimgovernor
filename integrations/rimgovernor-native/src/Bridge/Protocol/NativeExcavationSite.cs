#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

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
            foreach (var cell in request.Cells) if (!HasCell(cell) || !seen.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault()))) return false;
            return true;
        }
        private static bool HasCell(Common.Cell? cell) => cell != null && cell.X is not null && cell.Z is not null && cell.X.GetValueOrDefault() >= 0 && cell.Z.GetValueOrDefault() >= 0;

        internal static Obs.ExcavationCell Row(IntVec3 cell, Map map, Common.ObservationContext context)
        {
            var row = new Obs.ExcavationCell { Cell = new Common.Cell { X = cell.x, Z = cell.z }, Fogged = false, MineDesignated = false, Eligible = false };
            if (!cell.InBounds(map)) { row = row with { Eligible = false }; row = row with { Blocker = "Cell is outside the map" }; return row; }
            row = row with { Fogged = cell.Fogged(map) };
            row = row with { Walkable = cell.Walkable(map) };
            row = row with { MineDesignated = ExcavationTools.Designated(cell, map) };
            var rock = ExcavationTools.RockAt(cell, map);
            if (rock != null)
            {
                row = row with { MineableDefName = rock.def.defName };
            }
            var blocker = ExcavationTools.CellBlocker(cell, map);
            row = row with { Eligible = blocker == null && ExcavationTools.Eligible(cell, map) };
            if (blocker != null) row = row with { Blocker = blocker };
            else if (!row.Eligible.GetValueOrDefault()) row = row with { Blocker = "Native mining designation is not accepted at this cell" };
            return row;
        }

        internal static Obs.ExcavationSiteSnapshot Read(Map map, Obs.ExcavationSiteRequest request, Common.ObservationContext context)
        {
            var cells = request.Cells.Select(c => new IntVec3(c.X.GetValueOrDefault(), 0, c.Z.GetValueOrDefault())).ToList();
            var access = new IntVec3((request.AccessCell?.X).GetValueOrDefault(), 0, (request.AccessCell?.Z).GetValueOrDefault());
            var snapshot = new Obs.ExcavationSiteSnapshot { Context = context, CollapsePending = map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0 };
            foreach (var cell in cells) snapshot = snapshot with { Cells = [.. snapshot.Cells, Row(cell, map, context)] };
            var known = cells.Where(c => c.InBounds(map)).ToList();
            if (known.Count == 0) { snapshot = snapshot with { SupportAfterRemoval = Obs.ExcavationSupport.Unknown }; snapshot = snapshot with { SupportBlocker = "Unknown excavation geometry" }; }
            else
            {
                var support = ExcavationSafety.Check(map, known, out var checkedRoofs, out var blocker, throughFog: true);
                snapshot = snapshot with { RoofCellsChecked = (uint)checkedRoofs };
                snapshot = snapshot with { SupportAfterRemoval = support == ExcavationSafety.Support.Supported ? Obs.ExcavationSupport.Supported
                    : support == ExcavationSafety.Support.Unknown ? Obs.ExcavationSupport.Unknown : Obs.ExcavationSupport.Unsupported };
                if (blocker != null) snapshot = snapshot with { SupportBlocker = blocker };
            }
            if (snapshot.CollapsePending.GetValueOrDefault())
            { snapshot = snapshot with { SupportAfterRemoval = Obs.ExcavationSupport.Unsupported }; snapshot = snapshot with { SupportBlocker = "Roof collapse is already pending" }; }
            // The access cell may itself be rock (single-cell dispatch reads pass
            // the target): a miner stands on any visible walkable neighbour.
            var stand = ExcavationTools.StandingCell(access, map);
            // A standing cell nobody can path to (the mouth walled shut, the
            // pocket beyond it enclosed) is not reachable access; only mobile
            // colonists can say so, an all-downed colony leaves it standing.
            var mobile = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).ToList();
            snapshot = snapshot with { AccessReachable = stand.IsValid && (mobile.Count == 0 || mobile.Any(p => p.CanReach(stand, PathEndMode.OnCell, Danger.None))) };
            // A door threshold still inside the mountain has no standing
            // cell, but the dig runs from the open front: a miner mines the
            // rock beside any open cell it reaches, then the next cell
            //.
            if (!snapshot.AccessReachable.GetValueOrDefault() && mobile.Count > 0)
            {
                var fronts = cells.Where(c => c.InBounds(map)).SelectMany(c => GenAdj.CardinalDirections.Select(o => c + o))
                    .Where(n => n.InBounds(map) && !n.Fogged(map) && n.Walkable(map) && mobile.Any(p => p.CanReach(n, PathEndMode.OnCell, Danger.None))).Take(1).ToList();
                if (fronts.Count > 0) { stand = fronts[0]; snapshot = snapshot with { AccessReachable = true }; }
            }
            var workers = snapshot.AccessReachable.GetValueOrDefault() ? ExcavationTools.Workers(map, stand) : new List<Pawn>();
            snapshot = snapshot with { WorkerAvailable = workers.Count > 0 };
            foreach (var worker in workers) snapshot = snapshot with { WorkerIds = [.. snapshot.WorkerIds, worker.GetUniqueLoadID()] };
            return snapshot;
        }
    }
}
