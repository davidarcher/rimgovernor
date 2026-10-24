#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// rimgovernor/combat_geometry: DecideCombat's one geometry read
    /// per stop, by the game's own rules so the brain never reimplements cover,
    /// sight or pathing. For each candidate cell and each hostile: the cover
    /// at the cell against a shot from the hostile's cell
    /// (CoverUtility.CalculateOverallBlockChance), line of fire
    /// (GenSight.LineOfSight), and whether a colonist stands on that line.
    /// Read-only, one main-thread hop. A request it cannot answer is refused
    /// (NativeFaults.Refused); the reply is always the whole geometry.
    /// </summary>
    internal static class NativeCombatGeometryTools
    {
        /// The caps, measured on lab-ranged against the 50 ms main-thread
        /// budget: 64 cells x 8 pawns took 1.3-6.6 ms of main thread
        /// (worst of 5 per run), about 13 ms at the caps.
        /// Kept (docs/developers/contracts/kept-constants.md).
        internal const int MaxCells = 64;
        internal const int MaxHostiles = 16;
        /// A firing_cells proposal's search radius cap.
        internal const int MaxRadius = 12;

        internal static Mirror.CombatGeometry HandleRead(Mirror.CombatGeometryRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out Map? map, out var context, out var stale)) throw NativeFaults.Refused(stale);
            try { return Geometry(map, request, context); }
            catch (GeometryRefused refused) { throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refused.Message)); }
        }

        private sealed class GeometryRefused : Exception { public GeometryRefused(string message) : base(message) { } }

        internal static bool Validate(Mirror.CombatGeometryRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A combat geometry read requires a complete identity, 1.." + MaxCells
                + " distinct cells and 1.." + MaxHostiles + " distinct hostile ids.");
            if (!ProtoBoundary.Complete(request.Identity)) return false;
            if (request.Propose != null)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A combat geometry propose block requires one role with a valid anchor (line 1.." + MaxCells
                    + " distinct cells; a choke and a distinct our_side cell; 1.." + MaxHostiles + " distinct targets, a from cell and radius 1.." + MaxRadius
                    + "; a rescuer and a to cell) and at most " + (MaxCells - 1) + " named cells.");
                if (request.Cells.Count >= MaxCells || !ValidatePropose(request.Propose)) return false;
            }
            // Named cells alone need no hostile: their standability.
            var hostilesMin = request.Propose == null ? 0 : 1;
            if (request.Cells.Count < (request.Propose == null ? 1 : 0) || request.Cells.Count > MaxCells || request.HostileIds.Count < hostilesMin || request.HostileIds.Count > MaxHostiles) return false;
            if (!request.Cells.All(ValidCell) || request.Cells.Select(c => (c.X, c.Z)).Distinct().Count() != request.Cells.Count) return false;
            if (request.HostileIds.Any(id => !ProtoBoundary.IsIdentifier(id.Value)) || request.HostileIds.Distinct().Count() != request.HostileIds.Count) return false;
            return true;
        }

        private static bool ValidCell(Common.Cell c) => c != null && c.X >= 0 && c.Z >= 0;

        private static bool ValidCells(RimGovernor.Brain.Domain.EquatableArray<Common.Cell> cells, int max) =>
            cells.Count >= 1 && cells.Count <= max && cells.All(ValidCell) && cells.Select(c => (c.X, c.Z)).Distinct().Count() == cells.Count;

        /// A propose block: named cells may be 0..MaxCells-1, since
        /// named plus proposed share MaxCells; each role's anchor is checked.
        private static bool ValidatePropose(Mirror.CombatGeometryPropose p)
        {
            switch (p)
            {
                case Mirror.CombatGeometryPropose.CoverBehindLine cover:
                    return ValidCells(cover.Line, MaxCells);
                case Mirror.CombatGeometryPropose.AdjacentToChoke a:
                    return ValidCell(a.Choke) && ValidCell(a.OurSide) && !(a.Choke.X == a.OurSide.X && a.Choke.Z == a.OurSide.Z);
                case Mirror.CombatGeometryPropose.FiringCells f:
                    return ValidCell(f.From) && f.Radius >= 1 && f.Radius <= MaxRadius && ValidCells(f.Targets, MaxHostiles);
                case Mirror.CombatGeometryPropose.RescuePath rescue:
                    return ValidCell(rescue.To) && ProtoBoundary.IsIdentifier(rescue.Pawn.Value);
            }
            return false;
        }

        private static Mirror.CombatGeometry Geometry(Map map, Mirror.CombatGeometryRequest request, Common.ObservationContext context)
        {
            var clock = Stopwatch.StartNew();
            var pawns = new Dictionary<string, Pawn>(StringComparer.Ordinal);
            foreach (var p in map.mapPawns.AllPawnsSpawned) pawns[CombatMirror.LoadId(p)] = p;
            var hostiles = new List<Pawn>();
            foreach (var id in request.HostileIds)
            {
                if (!pawns.TryGetValue(id.Value, out var h)) throw new GeometryRefused("Hostile " + id.Value + " is not spawned on the map.");
                hostiles.Add(h);
            }
            var colonists = new HashSet<IntVec3>(map.mapPawns.FreeColonistsSpawned.Select(c => c.Position));
            var cells = request.Cells.Select(wire => Score(map, InBounds(map, wire), hostiles, colonists)).ToList();
            var proposed = new List<Mirror.CombatGeometryCell>();
            if (request.Propose is Mirror.CombatGeometryPropose.RescuePath rescue)
            {
                if (!pawns.TryGetValue(rescue.Pawn.Value, out var walker)) throw new GeometryRefused("Pawn " + rescue.Pawn.Value + " is not spawned on the map.");
                foreach (var cell in Route(map, walker, InBounds(map, rescue.To)).Take(MaxCells - request.Cells.Count))
                    proposed.Add(Score(map, cell, hostiles, colonists));
            }
            else if (request.Propose != null)
            {
                var named = new HashSet<IntVec3>(request.Cells.Select(w => new IntVec3(w.X, 0, w.Z)));
                foreach (var cell in Propose(map, request.Propose, hostiles, named).Take(MaxCells - request.Cells.Count))
                    proposed.Add(Score(map, cell, hostiles, colonists));
            }
            return new Mirror.CombatGeometry { Context = context, Cells = [.. cells], Proposed = [.. proposed], MainThreadMs = clock.Elapsed.TotalMilliseconds };
        }

        private static IntVec3 InBounds(Map map, Common.Cell wire)
        {
            var cell = new IntVec3(wire.X, 0, wire.Z);
            if (!cell.InBounds(map)) throw new GeometryRefused("Cell " + wire.X + "," + wire.Z + " is out of bounds.");
            return cell;
        }

        private static float BestCover(Map map, IntVec3 cell, List<Pawn> hostiles) =>
            hostiles.Max(h => CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(cell), h.Position, map));

        private static int DistanceTo(IntVec3 cell, IEnumerable<IntVec3> anchors) => anchors.Min(a => cell.DistanceToSquared(a));

        /// The role's candidates, best first: standable, in bounds,
        /// not named. Ties break on (x, z) so the order is stable.
        private static IEnumerable<IntVec3> Propose(Map map, Mirror.CombatGeometryPropose propose, List<Pawn> hostiles, HashSet<IntVec3> named)
        {
            bool Candidate(IntVec3 c) => c.InBounds(map) && !named.Contains(c) && c.Standable(map);
            switch (propose)
            {
                case Mirror.CombatGeometryPropose.CoverBehindLine coverBehind:
                {
                    var line = coverBehind.Line.Select(w => InBounds(map, w)).ToList();
                    var onLine = new HashSet<IntVec3>(line);
                    return line.SelectMany(l => GenAdj.AdjacentCells.Select(d => l + d)).Distinct()
                        .Where(c => !onLine.Contains(c) && Candidate(c))
                        .Select(c => (cell: c, cover: BestCover(map, c, hostiles))).Where(t => t.cover > 0f).ToList()
                        .OrderByDescending(t => t.cover).ThenBy(t => DistanceTo(t.cell, line)).ThenBy(t => t.cell.x).ThenBy(t => t.cell.z).Select(t => t.cell);
                }
                case Mirror.CombatGeometryPropose.AdjacentToChoke adjacent:
                {
                    var choke = InBounds(map, adjacent.Choke);
                    var side = InBounds(map, adjacent.OurSide);
                    var limit = choke.DistanceToSquared(side);
                    return GenAdj.AdjacentCells.Select(d => choke + d).Where(c => Candidate(c) && c.DistanceToSquared(side) < limit).ToList()
                        .OrderBy(c => c.DistanceToSquared(side)).ThenBy(c => c.x).ThenBy(c => c.z);
                }
                case Mirror.CombatGeometryPropose.FiringCells f:
                {
                    var radius = f.Radius;
                    var from = InBounds(map, f.From);
                    var targets = f.Targets.Select(w => InBounds(map, w)).ToList();
                    var found = new List<(IntVec3 cell, int sight, float cover)>();
                    for (int dx = -radius; dx <= radius; dx++)
                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            if (dx * dx + dz * dz > radius * radius) continue;
                            var c = new IntVec3(from.x + dx, 0, from.z + dz);
                            if (!Candidate(c)) continue;
                            var sight = targets.Count(t => GenSight.LineOfSight(c, t, map, true));
                            if (sight > 0) found.Add((c, sight, BestCover(map, c, hostiles)));
                        }
                    return found.OrderByDescending(t => t.sight).ThenByDescending(t => t.cover).ThenBy(t => t.cell.DistanceToSquared(from))
                        .ThenBy(t => t.cell.x).ThenBy(t => t.cell.z).Select(t => t.cell);
                }
            }
            throw new GeometryRefused("A propose block needs a role.");
        }

        /// The rescue_path role: the pawn's route to `to` by the game
        /// pathfinder, in walking order without the start cell; empty with no
        /// path. PawnPath.Peek(0) is the start cell.
        private static List<IntVec3> Route(Map map, Pawn walker, IntVec3 to)
        {
            var route = new List<IntVec3>();
            using (var path = map.pathFinder.FindPathNow(walker.Position, new LocalTargetInfo(to), TraverseParms.For(walker, Danger.Deadly), peMode: PathEndMode.Touch))
            {
                if (path == null || !path.Found) return route;
                for (int i = 0; i < path.NodesLeftCount; i++)
                {
                    var node = path.Peek(i);
                    if (node != walker.Position) route.Add(node);
                }
            }
            return route;
        }

        private static Mirror.CombatGeometryCell Score(Map map, IntVec3 cell, List<Pawn> hostiles, HashSet<IntVec3> colonists)
        {
            var lines = hostiles.Select(h => new Mirror.CombatSightLine
            {
                HostileId = new PawnId(CombatMirror.LoadId(h)),
                Cover = CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(cell), h.Position, map),
                HostileCover = CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(h.Position), cell, map),
                LineOfFire = GenSight.LineOfSight(cell, h.Position, map, true),
                ColonistInPath = GenSight.PointsOnLineOfSight(cell, h.Position).Any(p => p != cell && p != h.Position && colonists.Contains(p)),
            }).ToList();
            return new Mirror.CombatGeometryCell
            {
                Cell = new Common.Cell { X = cell.x, Z = cell.z },
                Lines = [.. lines],
                Standable = cell.Standable(map),
                HostileLineOfFire = lines.Any(l => l.LineOfFire),
                Door = cell.GetEdifice(map) is Building_Door door && door.Faction == Faction.OfPlayerSilentFail,
            };
        }
    }
}
