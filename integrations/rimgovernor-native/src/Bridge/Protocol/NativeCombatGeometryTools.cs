#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Mirror = RimGovernor.Protocol.Mirror;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// rimgovernor/combat_geometry (#851): DecideCombat's one geometry read
    /// per stop, by the game's own rules so Go never reimplements cover,
    /// sight or pathing. For each candidate cell and each hostile: the cover
    /// at the cell against a shot from the hostile's cell
    /// (CoverUtility.CalculateOverallBlockChance), line of fire
    /// (GenSight.LineOfSight), and whether a colonist stands on that line;
    /// and, for a named pawn, the path ticks to each cell. Read-only, one
    /// main-thread hop.
    /// </summary>
    public sealed class NativeCombatGeometryTools
    {
        private const string ToolName = "rimgovernor/combat_geometry";
        /// The caps, measured on lab-ranged against the 50 ms main-thread
        /// budget (#851): 64 cells x 8 pawns took 1.3-6.6 ms of main thread
        /// (worst of 5 per run, path ticks included), about 13 ms at the caps.
        internal const int MaxCells = 64;
        internal const int MaxHostiles = 16;
        /// A firing_cells proposal's search radius cap (#871).
        internal const int MaxRadius = 12;

        [Tool(ToolName, Title = "Read combat geometry",
            Description = "Official CombatGeometryRequest ProtoJSON. Cover, line of fire and colonist-in-path from each candidate cell to each hostile, and a named pawn's path ticks to each cell. An optional propose block adds ranked candidate cells for one role (cover_behind_line, adjacent_to_choke, firing_cells), scored alike. At most 64 cells (named plus proposed) and 16 hostiles. Read-only.")]
        [ToolResponse("payload", "string", "Official mirror CombatGeometryReply ProtoJSON.", Always = true)]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a CombatGeometryRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Mirror.CombatGeometryRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Mirror.CombatGeometryReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Identity, out Map? map, out var context, out var stale))
                    return ProtoBoundary.Encode(new Mirror.CombatGeometryReply { Failure = stale });
                try { return ProtoBoundary.Encode(new Mirror.CombatGeometryReply { Observed = Geometry(map, parsed, context) }); }
                catch (GeometryRefused refused) { return ProtoBoundary.Encode(new Mirror.CombatGeometryReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refused.Message) }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        private sealed class GeometryRefused : Exception { public GeometryRefused(string message) : base(message) { } }

        internal static bool Validate(Mirror.CombatGeometryRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A combat geometry read requires a complete identity, 1.." + MaxCells
                + " distinct cells, 1.." + MaxHostiles + " distinct hostile ids and an optional pawn id.");
            if (!ProtoBoundary.Complete(request.Identity)) return false;
            if (request.Propose != null)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A combat geometry propose block requires one role with a valid anchor (line 1.." + MaxCells
                    + " distinct cells; a choke and a distinct our_side cell; 1.." + MaxHostiles + " distinct targets, a from cell and radius 1.." + MaxRadius
                    + ") and at most " + (MaxCells - 1) + " named cells.");
                if (request.Cells.Count >= MaxCells || !ValidatePropose(request.Propose)) return false;
            }
            if (request.Cells.Count < (request.Propose == null ? 1 : 0) || request.Cells.Count > MaxCells || request.HostileIds.Count < 1 || request.HostileIds.Count > MaxHostiles) return false;
            if (request.Cells.Any(c => c == null || !c.HasX || !c.HasZ) || request.Cells.Select(c => (c.X, c.Z)).Distinct().Count() != request.Cells.Count) return false;
            if (request.HostileIds.Any(id => !ProtoBoundary.IsIdentifier(id)) || request.HostileIds.Distinct(StringComparer.Ordinal).Count() != request.HostileIds.Count) return false;
            if (request.HasPawnId && !ProtoBoundary.IsIdentifier(request.PawnId)) return false;
            return true;
        }

        private static bool ValidCell(Common.Cell? c) => c != null && c.HasX && c.HasZ && c.X >= 0 && c.Z >= 0;

        private static bool ValidCells(IList<Common.Cell> cells, int max) =>
            cells.Count >= 1 && cells.Count <= max && cells.All(ValidCell) && cells.Select(c => (c.X, c.Z)).Distinct().Count() == cells.Count;

        /// A propose block (#871): named cells may be 0..MaxCells-1, since
        /// named plus proposed share MaxCells; each role's anchor is checked.
        private static bool ValidatePropose(Mirror.CombatGeometryPropose p)
        {
            switch (p.RoleCase)
            {
                case Mirror.CombatGeometryPropose.RoleOneofCase.CoverBehindLine:
                    return ValidCells(p.CoverBehindLine.Line, MaxCells);
                case Mirror.CombatGeometryPropose.RoleOneofCase.AdjacentToChoke:
                    var a = p.AdjacentToChoke;
                    return ValidCell(a.Choke) && ValidCell(a.OurSide) && !(a.Choke.X == a.OurSide.X && a.Choke.Z == a.OurSide.Z);
                case Mirror.CombatGeometryPropose.RoleOneofCase.FiringCells:
                    var f = p.FiringCells;
                    return ValidCell(f.From) && f.HasRadius && f.Radius >= 1 && f.Radius <= MaxRadius && ValidCells(f.Targets, MaxHostiles);
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
                if (!pawns.TryGetValue(id, out var h)) throw new GeometryRefused("Hostile " + id + " is not spawned on the map.");
                hostiles.Add(h);
            }
            Pawn? walker = null;
            if (request.HasPawnId && !pawns.TryGetValue(request.PawnId, out walker)) throw new GeometryRefused("Pawn " + request.PawnId + " is not spawned on the map.");
            var colonists = new HashSet<IntVec3>(map.mapPawns.FreeColonistsSpawned.Select(c => c.Position));
            var observed = new Mirror.CombatGeometry { Context = context };
            foreach (var wire in request.Cells)
                observed.Cells.Add(Score(map, InBounds(map, wire), wire.Clone(), hostiles, colonists, walker));
            if (request.Propose != null)
            {
                var named = new HashSet<IntVec3>(request.Cells.Select(w => new IntVec3(w.X, 0, w.Z)));
                foreach (var cell in Propose(map, request.Propose, hostiles, named).Take(MaxCells - request.Cells.Count))
                    observed.Proposed.Add(Score(map, cell, new Common.Cell { X = cell.x, Z = cell.z }, hostiles, colonists, walker));
            }
            observed.MainThreadMs = clock.Elapsed.TotalMilliseconds;
            return observed;
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

        /// The role's candidates (#871), best first: standable, in bounds,
        /// not named. Ties break on (x, z) so the order is stable.
        private static IEnumerable<IntVec3> Propose(Map map, Mirror.CombatGeometryPropose propose, List<Pawn> hostiles, HashSet<IntVec3> named)
        {
            bool Candidate(IntVec3 c) => c.InBounds(map) && !named.Contains(c) && c.Standable(map);
            switch (propose.RoleCase)
            {
                case Mirror.CombatGeometryPropose.RoleOneofCase.CoverBehindLine:
                {
                    var line = propose.CoverBehindLine.Line.Select(w => InBounds(map, w)).ToList();
                    var onLine = new HashSet<IntVec3>(line);
                    return line.SelectMany(l => GenAdj.AdjacentCells.Select(d => l + d)).Distinct()
                        .Where(c => !onLine.Contains(c) && Candidate(c))
                        .Select(c => (cell: c, cover: BestCover(map, c, hostiles))).Where(t => t.cover > 0f).ToList()
                        .OrderByDescending(t => t.cover).ThenBy(t => DistanceTo(t.cell, line)).ThenBy(t => t.cell.x).ThenBy(t => t.cell.z).Select(t => t.cell);
                }
                case Mirror.CombatGeometryPropose.RoleOneofCase.AdjacentToChoke:
                {
                    var choke = InBounds(map, propose.AdjacentToChoke.Choke);
                    var side = InBounds(map, propose.AdjacentToChoke.OurSide);
                    var limit = choke.DistanceToSquared(side);
                    return GenAdj.AdjacentCells.Select(d => choke + d).Where(c => Candidate(c) && c.DistanceToSquared(side) < limit).ToList()
                        .OrderBy(c => c.DistanceToSquared(side)).ThenBy(c => c.x).ThenBy(c => c.z);
                }
                case Mirror.CombatGeometryPropose.RoleOneofCase.FiringCells:
                {
                    var f = propose.FiringCells;
                    var from = InBounds(map, f.From);
                    var targets = f.Targets.Select(w => InBounds(map, w)).ToList();
                    var found = new List<(IntVec3 cell, int sight, float cover)>();
                    for (int dx = -f.Radius; dx <= f.Radius; dx++)
                        for (int dz = -f.Radius; dz <= f.Radius; dz++)
                        {
                            if (dx * dx + dz * dz > f.Radius * f.Radius) continue;
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

        private static Mirror.CombatGeometryCell Score(Map map, IntVec3 cell, Common.Cell wire, List<Pawn> hostiles, HashSet<IntVec3> colonists, Pawn? walker)
        {
            var row = new Mirror.CombatGeometryCell { Cell = wire, Standable = cell.Standable(map) };
                foreach (var h in hostiles)
                {
                    var line = new Mirror.CombatSightLine { HostileId = CombatMirror.LoadId(h),
                        Cover = CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(cell), h.Position, map),
                        HostileCover = CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(h.Position), cell, map),
                        LineOfFire = GenSight.LineOfSight(cell, h.Position, map, true) };
                    line.ColonistInPath = GenSight.PointsOnLineOfSight(cell, h.Position).Any(p => p != cell && p != h.Position && colonists.Contains(p));
                    row.Lines.Add(line);
                }
                if (walker != null)
                {
                    using (var path = map.pathFinder.FindPathNow(walker.Position, new LocalTargetInfo(cell), TraverseParms.For(walker, Danger.Deadly), peMode: PathEndMode.OnCell))
                        if (path.Found) row.PathTicks = (int)Math.Min(int.MaxValue, path.TotalCost);
                }
                return row;
        }
    }
}
