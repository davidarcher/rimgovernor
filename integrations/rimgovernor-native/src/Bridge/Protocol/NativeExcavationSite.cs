using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
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
            foreach (var cell in request.Cells) if (!HasCell(cell) || !seen.Add((cell.X, cell.Z))) return false;
            return true;
        }
        private static bool HasCell(Common.Cell? cell) => cell != null && cell.X >= 0 && cell.Z >= 0;

        internal static Obs.ExcavationCell Row(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map))
                return new Obs.ExcavationCell { Cell = new DomainCell(cell.x, cell.z), Fogged = false, Definition = null, Walkable = false, MineDesignated = false, Eligible = false };
            var eligible = ExcavationTools.Eligible(cell, map);
            return new Obs.ExcavationCell {
                Cell = new DomainCell(cell.x, cell.z),
                Fogged = cell.Fogged(map),
                Definition = ExcavationTools.RockAt(cell, map)?.def.defName,
                Walkable = cell.Walkable(map),
                MineDesignated = ExcavationTools.Designated(cell, map),
                Eligible = eligible,
            };
        }

        // A request with no cell on the map is refused before Read: the site has no geometry to judge.
        internal static bool OnMap(Obs.ExcavationSiteRequest request, Map map, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At least one requested excavation cell must be on the map.");
            return request.Cells.Any(c => new IntVec3(c.X, 0, c.Z).InBounds(map));
        }

        internal static Obs.ExcavationSiteSnapshot Read(Map map, Obs.ExcavationSiteRequest request, Common.ObservationContext context)
        {
            var cells = request.Cells.Select(c => new IntVec3(c.X, 0, c.Z)).ToList();
            var access = new IntVec3((request.AccessCell ?? throw new System.InvalidOperationException("Required native value is unavailable: request.AccessCell.")).X, 0, request.AccessCell.Z);
            var collapsePending = map.roofCollapseBuffer.CellsMarkedToCollapse.Count > 0;
            var rows = cells.Select(c => Row(c, map)).ToList();
            var known = cells.Where(c => c.InBounds(map)).ToList();
            // Through fog the support check judges every roof it walks, so it never answers Unknown.
            var check = ExcavationSafety.Check(map, known, out var supportBlocker, throughFog: true);
            if (check == ExcavationSafety.Support.Unknown) throw new InvalidOperationException("Excavation support through fog answered unknown.");
            var support = check == ExcavationSafety.Support.Supported ? Obs.ExcavationSupport.Supported : Obs.ExcavationSupport.Unsupported;
            if (collapsePending) { support = Obs.ExcavationSupport.Unsupported; supportBlocker = "Roof collapse is already pending"; }
            // The access cell may itself be rock (single-cell dispatch reads pass
            // the target): a miner stands on any visible walkable neighbour.
            var stand = ExcavationTools.StandingCell(access, map);
            // A standing cell nobody can path to (the mouth walled shut, the
            // pocket beyond it enclosed) is not reachable access; only mobile
            // colonists can say so, an all-downed colony leaves it standing.
            var mobile = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed).ToList();
            var reachable = stand.IsValid && (mobile.Count == 0 || mobile.Any(p => p.CanReach(stand, PathEndMode.OnCell, Danger.None)));
            // A door threshold still inside the mountain has no standing
            // cell, but the dig runs from the open front: a miner mines the
            // rock beside any open cell it reaches, then the next cell
            //.
            if (!reachable && mobile.Count > 0)
            {
                var fronts = known.SelectMany(c => GenAdj.CardinalDirections.Select(o => c + o))
                    .Where(n => n.InBounds(map) && !n.Fogged(map) && n.Walkable(map) && mobile.Any(p => p.CanReach(n, PathEndMode.OnCell, Danger.None))).Take(1).ToList();
                if (fronts.Count > 0) { stand = fronts[0]; reachable = true; }
            }
            var workers = reachable ? ExcavationTools.Workers(map, stand) : new List<Pawn>();
            return new Obs.ExcavationSiteSnapshot {
                Context = context, Cells = [.. rows], Support = support, SupportBlocker = supportBlocker,
                CollapsePending = collapsePending, Workers = [.. workers.Select(w => w.GetUniqueLoadID())], AccessReachable = reachable,
            };
        }
    }
}
