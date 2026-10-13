#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// rimgovernor/combat_geometry: DecideCombat's one geometry read
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
        /// budget: 64 cells x 8 pawns took 1.3-6.6 ms of main thread
        /// (worst of 5 per run, path ticks included), about 13 ms at the caps.
        /// Kept (docs/developers/contracts/kept-constants.md).
        internal const int MaxCells = 64;
        internal const int MaxHostiles = 16;
        /// A firing_cells proposal's search radius cap.
        internal const int MaxRadius = 12;

        [Tool(ToolName, Title = "Read combat geometry",
            Description = "Official CombatGeometryRequest ProtoJSON. Cover, line of fire and colonist-in-path from each candidate cell to each hostile, and a named pawn's path ticks to each cell. An optional propose block adds ranked candidate cells for one role (cover_behind_line, adjacent_to_choke, firing_cells), scored alike; rescue_path returns the named pawn's route cells with a hostile line-of-fire and door flag each. At most 64 cells (named plus proposed) and 16 hostiles. Read-only.")]
        [ToolResponse("payload", "string", "Official mirror CombatGeometryReply ProtoJSON.", Always = true)]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a CombatGeometryRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse<Mirror.CombatGeometryRequest>(ctx, ToolName, request!, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Mirror.CombatGeometryReply { Outcome = new Mirror.CombatGeometryReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleRead(parsed), cancellationToken).ConfigureAwait(false);
        }

        internal static Mirror.CombatGeometryReply HandleRead(Mirror.CombatGeometryRequest request)
        {
            if (!Validate(request, out var failure0)) return new Mirror.CombatGeometryReply { Outcome = new Mirror.CombatGeometryReplyOutcome.FailureCase(failure0) };
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out Map? map, out var context, out var stale))
                return new Mirror.CombatGeometryReply { Outcome = stale };
            try { return new Mirror.CombatGeometryReply { Outcome = Geometry(map, request, context) }; }
            catch (GeometryRefused refused) { return new Mirror.CombatGeometryReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refused.Message) }; }
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
                if (request.Cells.Count >= MaxCells || !ValidatePropose(request.Propose)
                    || request.Propose.Role is Mirror.CombatGeometryProposeRole.RescuePathCase && request.PawnId is null) return false;
            }
            // Named cells alone need no hostile: their standability.
            var hostilesMin = request.Propose == null && request.PawnId is null ? 0 : 1;
            if (request.Cells.Count < (request.Propose == null ? 1 : 0) || request.Cells.Count > MaxCells || request.HostileIds.Count < hostilesMin || request.HostileIds.Count > MaxHostiles) return false;
            if (request.Cells.Any(c => c == null || c.X is null || c.Z is null) || request.Cells.Select(c => (c.X.GetValueOrDefault(), c.Z.GetValueOrDefault())).Distinct().Count() != request.Cells.Count) return false;
            if (request.HostileIds.Any(id => !ProtoBoundary.IsIdentifier(id)) || request.HostileIds.Distinct(StringComparer.Ordinal).Count() != request.HostileIds.Count) return false;
            if (request.PawnId is not null && !ProtoBoundary.IsIdentifier(request.PawnId)) return false;
            return true;
        }

        private static bool ValidCell(Common.Cell? c) => c != null && c.X is not null && c.Z is not null && c.X.GetValueOrDefault() >= 0 && c.Z.GetValueOrDefault() >= 0;

        private static bool ValidCells(RimGovernor.Brain.Domain.EquatableArray<Common.Cell> cells, int max) =>
            cells.Count >= 1 && cells.Count <= max && cells.All(ValidCell) && cells.Select(c => (c.X.GetValueOrDefault(), c.Z.GetValueOrDefault())).Distinct().Count() == cells.Count;

        /// A propose block: named cells may be 0..MaxCells-1, since
        /// named plus proposed share MaxCells; each role's anchor is checked.
        private static bool ValidatePropose(Mirror.CombatGeometryPropose p)
        {
            switch (p.Role)
            {
                case Mirror.CombatGeometryProposeRole.CoverBehindLineCase { Value: var cover }:
                    return ValidCells(cover.Line, MaxCells);
                case Mirror.CombatGeometryProposeRole.AdjacentToChokeCase { Value: var a }:
                    return ValidCell(a.Choke) && ValidCell(a.OurSide) && !(a.Choke?.X == a.OurSide?.X && a.Choke?.Z == a.OurSide?.Z);
                case Mirror.CombatGeometryProposeRole.FiringCellsCase { Value: var f }:
                    return ValidCell(f.From) && f.Radius is >= 1 && f.Radius <= MaxRadius && ValidCells(f.Targets, MaxHostiles);
                case Mirror.CombatGeometryProposeRole.RescuePathCase { Value: var rescue }:
                    return ValidCell(rescue.To);
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
            if (request.PawnId is not null && !pawns.TryGetValue((request.PawnId ?? ""), out walker)) throw new GeometryRefused("Pawn " + (request.PawnId ?? "") + " is not spawned on the map.");
            var colonists = new HashSet<IntVec3>(map.mapPawns.FreeColonistsSpawned.Select(c => c.Position));
            var observed = new Mirror.CombatGeometry { Context = context };
            foreach (var wire in request.Cells)
                observed = observed with { Cells = [.. observed.Cells, Score(map, InBounds(map, wire), wire, hostiles, colonists, walker)] };
            if (request.Propose?.Role is Mirror.CombatGeometryProposeRole.RescuePathCase { Value: var rescue })
            {
                foreach (var cell in Route(map, walker!, InBounds(map, rescue.To)).Take(MaxCells - request.Cells.Count))
                    observed = observed with { Proposed = [.. observed.Proposed, Score(map, cell, new Common.Cell { X = cell.x, Z = cell.z }, hostiles, colonists, null)] };
            }
            else if (request.Propose != null)
            {
                var named = new HashSet<IntVec3>(request.Cells.Select(w => new IntVec3(w.X.GetValueOrDefault(), 0, w.Z.GetValueOrDefault())));
                foreach (var cell in Propose(map, request.Propose, hostiles, named).Take(MaxCells - request.Cells.Count))
                    observed = observed with { Proposed = [.. observed.Proposed, Score(map, cell, new Common.Cell { X = cell.x, Z = cell.z }, hostiles, colonists, walker)] };
            }
            observed = observed with { MainThreadMs = clock.Elapsed.TotalMilliseconds };
            return observed;
        }

        private static IntVec3 InBounds(Map map, Common.Cell? wire)
        {
            if (wire is null) throw new GeometryRefused("A cell is required.");
            var cell = new IntVec3(wire.X.GetValueOrDefault(), 0, wire.Z.GetValueOrDefault());
            if (!cell.InBounds(map)) throw new GeometryRefused("Cell " + wire.X.GetValueOrDefault() + "," + wire.Z.GetValueOrDefault() + " is out of bounds.");
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
            switch (propose.Role)
            {
                case Mirror.CombatGeometryProposeRole.CoverBehindLineCase { Value: var coverBehind }:
                {
                    var line = coverBehind.Line.Select(w => InBounds(map, w)).ToList();
                    var onLine = new HashSet<IntVec3>(line);
                    return line.SelectMany(l => GenAdj.AdjacentCells.Select(d => l + d)).Distinct()
                        .Where(c => !onLine.Contains(c) && Candidate(c))
                        .Select(c => (cell: c, cover: BestCover(map, c, hostiles))).Where(t => t.cover > 0f).ToList()
                        .OrderByDescending(t => t.cover).ThenBy(t => DistanceTo(t.cell, line)).ThenBy(t => t.cell.x).ThenBy(t => t.cell.z).Select(t => t.cell);
                }
                case Mirror.CombatGeometryProposeRole.AdjacentToChokeCase { Value: var adjacent }:
                {
                    var choke = InBounds(map, adjacent.Choke);
                    var side = InBounds(map, adjacent.OurSide);
                    var limit = choke.DistanceToSquared(side);
                    return GenAdj.AdjacentCells.Select(d => choke + d).Where(c => Candidate(c) && c.DistanceToSquared(side) < limit).ToList()
                        .OrderBy(c => c.DistanceToSquared(side)).ThenBy(c => c.x).ThenBy(c => c.z);
                }
                case Mirror.CombatGeometryProposeRole.FiringCellsCase { Value: var f }:
                {
                    var radius = f.Radius.GetValueOrDefault();
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

        private static Mirror.CombatGeometryCell Score(Map map, IntVec3 cell, Common.Cell wire, List<Pawn> hostiles, HashSet<IntVec3> colonists, Pawn? walker)
        {
            var row = new Mirror.CombatGeometryCell { Cell = wire, Standable = cell.Standable(map) };
            row = row with { Door = cell.GetEdifice(map) is Building_Door door && door.Faction == Faction.OfPlayerSilentFail };
                foreach (var h in hostiles)
                {
                    var line = new Mirror.CombatSightLine { HostileId = CombatMirror.LoadId(h),
                        Cover = CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(cell), h.Position, map),
                        HostileCover = CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(h.Position), cell, map),
                        LineOfFire = GenSight.LineOfSight(cell, h.Position, map, true) };
                    line = line with { ColonistInPath = GenSight.PointsOnLineOfSight(cell, h.Position).Any(p => p != cell && p != h.Position && colonists.Contains(p)) };
                    row = row with { Lines = [.. row.Lines, line] };
                    if (line.LineOfFire.GetValueOrDefault()) row = row with { HostileLineOfFire = true };
                }
                if (walker != null)
                {
                    using (var path = map.pathFinder.FindPathNow(walker.Position, new LocalTargetInfo(cell), TraverseParms.For(walker, Danger.Deadly), peMode: PathEndMode.OnCell))
                        if (path.Found) row = row with { PathTicks = (int)Math.Min(int.MaxValue, path.TotalCost) };
                }
                return row;
        }
    }
}
