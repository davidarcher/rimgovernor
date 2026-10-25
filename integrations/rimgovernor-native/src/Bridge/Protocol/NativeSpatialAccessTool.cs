using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Contracts.Wire.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace HomeBridge.BridgeTools
{
    // Proto port of SpatialAccessTools.Inspect (home/spatial_access): the
    // read-only access audit that compares each mobile colonist's current
    // safe four-neighbour component with the same component once the
    // requested cells are impassable. Native CanReach answers the exact
    // target cells separately. No orders, path-grid edits or simulation.
    internal static class NativeSpatialAccessTool
    {
        // The one surviving cap: the per-colonist work scans the whole map. Over it the read
        // is unavailable (LimitExceeded), never partial.
        internal const long MaximumMapCells = 262144;

        internal static Obs.SpatialAccessSnapshot HandleReadSpatialAccess(Obs.SpatialAccessRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out failure))
                throw NativeFaults.Refused(failure);
            if ((long)map.Size.x * map.Size.z > MaximumMapCells)
                throw NativeFaults.Unavailable(Unavailable(Common.UnavailableReason.LimitExceeded, "Map exceeds the bounded spatial audit size."));
            var blocked = new HashSet<IntVec3>(request.BlockedCells.Select(Native));
            var targets = request.TargetCells.Select(Native).ToList();
            if (blocked.Concat(targets).Any(c => !c.InBounds(map)))
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cell is outside the current map."));
            try { return Audit(map, blocked, targets, request.PawnIds.ToList(), context, request.BudgetMs); }
            catch (ReadLimit error) { throw NativeFaults.Unavailable(Unavailable(Common.UnavailableReason.LimitExceeded, error.Message)); }
            catch (Exception) { throw NativeFaults.Unavailable(Unavailable(Common.UnavailableReason.ReadFailed, "Native spatial access could not be read completely.")); }
        }

        internal static bool Validate(Obs.SpatialAccessRequest request, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out Common.Failure? failure)
        {
            failure = null;
            if (request.BlockedCells.Concat(request.TargetCells).Any(c => c == null || c.X < 0 || c.Z < 0))
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Every cell needs nonnegative x and z.");
            else if (request.BlockedCells.Select(Native).Distinct().Count() != request.BlockedCells.Count || request.TargetCells.Select(Native).Distinct().Count() != request.TargetCells.Count)
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Duplicate cells.");
            else if (request.PawnIds.Any(string.IsNullOrWhiteSpace) || request.PawnIds.Distinct().Count() != request.PawnIds.Count)
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn ids must be unique and nonblank.");
            return failure == null;
        }

        private static HashSet<IntVec3> Reach(IntVec3 start, HashSet<IntVec3> allowed)
        {
            var found = new HashSet<IntVec3>();
            var queue = new Queue<IntVec3>();
            if (allowed.Contains(start)) { found.Add(start); queue.Enqueue(start); }
            while (queue.Count != 0)
            {
                var cell = queue.Dequeue();
                foreach (var next in new[] { cell + IntVec3.North, cell + IntVec3.South, cell + IntVec3.East, cell + IntVec3.West })
                    if (allowed.Contains(next) && found.Add(next)) queue.Enqueue(next);
            }
            return found;
        }

        private static Obs.SpatialAccessSnapshot Audit(Map map, HashSet<IntVec3> blocked, List<IntVec3> targets, List<string> pawnIds, Common.ObservationContext context, uint budgetMs)
        {
            var budgetTicks = budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000;
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.InMentalState).OrderBy(p => p.thingIDNumber).ToList();
            if (pawnIds.Count != 0)
            {
                var wanted = new HashSet<string>(pawnIds);
                pawns = pawns.Where(p => wanted.Contains(p.GetUniqueLoadID())).ToList();
                if (pawns.Count != pawnIds.Count) throw new ReadLimit("Every requested pawn id must name a spawned mobile free colonist.");
            }
            if (pawns.Count == 0) throw new ReadLimit("Require at least one mobile native colonist.");
            var terrain = map.AllCells.Where(c => !c.Fogged(map) && c.Walkable(map)).ToList();
            var rows = new List<Obs.PawnAccess>();
            foreach (var pawn in pawns)
            {
                var id = new PawnId(pawn.GetUniqueLoadID());
                var position = Cell(pawn.Position);
                // The budget is checked between colonists (about 70-80 ms each): one past
                // it is reported skipped, not audited and not assumed to keep access.
                if (System.Diagnostics.Stopwatch.GetTimestamp() - began >= budgetTicks)
                {
                    rows.Add(new Obs.PawnAccess.Skipped { Pawn = id, Position = position });
                    continue;
                }
                var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                var allowed = new HashSet<IntVec3>(terrain.Where(c => (area == null || area[c])
                    && c.GetDangerFor(pawn, map) == Danger.None
                    && (!(c.GetEdifice(map) is Building_Door door) || door.PawnCanOpen(pawn))));
                // The pawn is where it stands, even on a frame or a
                // blueprint under construction that is no longer walkable.
                allowed.Add(pawn.Position);
                var before = Reach(pawn.Position, allowed);
                allowed.ExceptWith(blocked);
                var origin = pawn.Position;
                if (blocked.Contains(origin))
                {
                    // Ordinary construction can move a pawn aside. Require a
                    // currently safe native exit, without moving the pawn here.
                    var footprint = Reach(origin, new HashSet<IntVec3>(before.Where(blocked.Contains)));
                    var frontier = footprint.SelectMany(c => new[] { c + IntVec3.North, c + IntVec3.South, c + IntVec3.East, c + IntVec3.West })
                        .Where(allowed.Contains).Distinct().OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();
                    origin = frontier.Where(c => pawn.CanReach(c, PathEndMode.OnCell, Danger.None)).Select(c => (IntVec3?)c).FirstOrDefault() ?? IntVec3.Invalid;
                }
                if (!origin.IsValid)
                {
                    rows.Add(new Obs.PawnAccess.Stranded { Pawn = id, Position = position });
                    continue;
                }
                var after = Reach(origin, allowed);
                rows.Add(new Obs.PawnAccess.Audited
                {
                    Pawn = id,
                    Position = position,
                    Targets = [.. targets.Select(c => new Obs.AccessTarget
                    {
                        Cell = Cell(c),
                        NativeReachable = pawn.CanReach(c, PathEndMode.OnCell, Danger.None),
                        ProjectedReachable = after.Contains(c),
                    })],
                });
            }
            return new Obs.SpatialAccessSnapshot { Context = context, Pawns = [.. rows] };
        }

        private static IntVec3 Native(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        private static DomainCell Cell(IntVec3 cell) => new DomainCell(cell.x, cell.z);
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}
