#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Proto port of SpatialAccessTools.Inspect (home/spatial_access): the
    // read-only access audit that compares each mobile colonist's current
    // safe four-neighbour component with the same component once the
    // requested cells are impassable. Native CanReach answers the exact
    // target cells separately. No orders, path-grid edits or simulation.
    public sealed class NativeSpatialAccessTool
    {
        internal const int MaximumBlockedCells = 16384;
        internal const int MaximumTargetCells = 128;
        internal const int MaximumPawns = 32;
        internal const int MaximumLostCells = 16;
        internal const long MaximumMapCells = 262144;

        [Tool("rimgovernor/observations_read_spatial_access", Title = "Read projected colony access",
            Description = "Official SpatialAccessRequest ProtoJSON. Paused-map audit of each mobile colonist's current safe four-neighbour reach against the same reach with blocked_cells impassable, plus native CanReach for target_cells. At most 16384 blocked and 128 target cells, 32 colonists; pawn_ids restricts the census. Unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations SpatialAccessReply ProtoJSON.", Always = true)]
        public async Task<object> ReadSpatialAccess(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a SpatialAccessRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_spatial_access", request!, Obs.SpatialAccessRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.SpatialAccessReply { Failure = failure });
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity!, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.SpatialAccessReply { Failure = failure });
                if (Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                    return ProtoBoundary.Encode(new Obs.SpatialAccessReply { Unavailable = Unavailable(Common.UnavailableReason.NotApplicable, "Spatial access requires a paused map.") });
                if ((long)map.Size.x * map.Size.z > MaximumMapCells)
                    return ProtoBoundary.Encode(new Obs.SpatialAccessReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, "Map exceeds the bounded spatial audit size.") });
                try {
                    var blocked = new HashSet<IntVec3>(parsed.BlockedCells.Select(Native));
                    var targets = parsed.TargetCells.Select(Native).ToList();
                    if (blocked.Concat(targets).Any(c => !c.InBounds(map))) return ProtoBoundary.Encode(new Obs.SpatialAccessReply {
                        Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cell is outside the current map.") });
                    return Encode(new Obs.SpatialAccessReply { Observed = Audit(map, blocked, targets, parsed.PawnIds.ToList(), context) });
                }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.SpatialAccessReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.SpatialAccessReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native spatial access could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.SpatialAccessRequest request, out Common.Failure failure)
        {
            failure = null!;
            if (request.BlockedCells.Count > MaximumBlockedCells) failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At most 16384 blocked cells.");
            else if (request.TargetCells.Count > MaximumTargetCells) failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At most 128 target cells.");
            else if (request.PawnIds.Count > MaximumPawns) failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At most 32 pawn ids.");
            else if (request.BlockedCells.Concat(request.TargetCells).Any(c => c == null || !c.HasX || !c.HasZ || c.X < 0 || c.Z < 0))
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Every cell needs nonnegative x and z.");
            else if (request.BlockedCells.Select(Native).Distinct().Count() != request.BlockedCells.Count || request.TargetCells.Select(Native).Distinct().Count() != request.TargetCells.Count)
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Duplicate cells.");
            else if (request.PawnIds.Any(string.IsNullOrWhiteSpace) || request.PawnIds.Distinct().Count() != request.PawnIds.Count)
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pawn ids must be unique and nonblank.");
            return failure == null;
        }

        private static HashSet<IntVec3> Reach(IntVec3 start, HashSet<IntVec3> allowed, out Dictionary<IntVec3, int> distance)
        {
            var found = new HashSet<IntVec3>();
            distance = new Dictionary<IntVec3, int>();
            var queue = new Queue<IntVec3>();
            if (allowed.Contains(start)) { found.Add(start); distance[start] = 0; queue.Enqueue(start); }
            while (queue.Count != 0)
            {
                var cell = queue.Dequeue();
                foreach (var next in new[] { cell + IntVec3.North, cell + IntVec3.South, cell + IntVec3.East, cell + IntVec3.West })
                    if (allowed.Contains(next) && found.Add(next)) { distance[next] = distance[cell] + 1; queue.Enqueue(next); }
            }
            return found;
        }

        private static Obs.SpatialAccessSnapshot Audit(Map map, HashSet<IntVec3> blocked, List<IntVec3> targets, List<string> pawnIds, Common.ObservationContext context)
        {
            var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.InMentalState).OrderBy(p => p.thingIDNumber).ToList();
            if (pawnIds.Count != 0)
            {
                var wanted = new HashSet<string>(pawnIds);
                pawns = pawns.Where(p => wanted.Contains(p.GetUniqueLoadID())).ToList();
                if (pawns.Count != pawnIds.Count) throw new ReadLimit("Every requested pawn id must name a spawned mobile free colonist.");
            }
            if (pawns.Count == 0 || pawns.Count > MaximumPawns) throw new ReadLimit("Require one through 32 mobile native colonists.");
            var terrain = map.AllCells.Where(c => !c.Fogged(map) && c.Walkable(map)).ToList();
            var result = new Obs.SpatialAccessSnapshot { Context = context, MapCells = (uint)(map.Size.x * map.Size.z), ObservedWalkableCells = (uint)terrain.Count, Completeness = Complete(pawns.Count) };
            foreach (var pawn in pawns)
            {
                var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                var allowed = new HashSet<IntVec3>(terrain.Where(c => (area == null || area[c])
                    && c.GetDangerFor(pawn, map) == Danger.None
                    && (!(c.GetEdifice(map) is Building_Door door) || door.PawnCanOpen(pawn))));
                var before = Reach(pawn.Position, allowed, out _);
                allowed.ExceptWith(blocked);
                var origin = pawn.Position;
                var egressSteps = 0;
                if (blocked.Contains(origin))
                {
                    // Ordinary construction can move a pawn aside. Require a
                    // currently safe native exit, without moving the pawn here.
                    var footprint = Reach(origin, new HashSet<IntVec3>(before.Where(blocked.Contains)), out var exits);
                    var frontier = footprint.SelectMany(c => new[] { c + IntVec3.North, c + IntVec3.South, c + IntVec3.East, c + IntVec3.West })
                        .Where(allowed.Contains).Distinct().OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();
                    origin = frontier.Where(c => pawn.CanReach(c, PathEndMode.OnCell, Danger.None)).Select(c => (IntVec3?)c).FirstOrDefault() ?? IntVec3.Invalid;
                    if (origin.IsValid)
                        egressSteps = exits.Where(pair => Math.Abs(pair.Key.x - origin.x) + Math.Abs(pair.Key.z - origin.z) == 1).Min(pair => pair.Value) + 1;
                }
                var distances = new Dictionary<IntVec3, int>();
                var after = origin.IsValid ? Reach(origin, allowed, out distances) : new HashSet<IntVec3>();
                var lost = before.Where(c => !blocked.Contains(c) && !after.Contains(c)).OrderBy(c => c.x).ThenBy(c => c.z).ToList();
                var row = new Obs.PawnAccess {
                    Pawn = new Obs.EntityRef { Id = pawn.GetUniqueLoadID(), DefName = pawn.def.defName, MapId = map.uniqueID, Position = Cell(pawn.Position) },
                    CurrentCells = (uint)before.Count, ProjectedCells = (uint)after.Count, LostCellCount = (uint)lost.Count, EgressSteps = (uint)egressSteps,
                    Completeness = Complete(targets.Count),
                };
                if (origin.IsValid) row.ProjectedOrigin = Cell(origin);
                foreach (var c in lost.Take(MaximumLostCells)) row.LostCells.Add(Cell(c));
                foreach (var c in targets)
                {
                    var projected = after.Contains(c);
                    var target = new Obs.AccessTarget { Cell = Cell(c), NativeReachable = pawn.CanReach(c, PathEndMode.OnCell, Danger.None), ProjectedReachable = projected };
                    if (projected) target.ProjectedSteps = (uint)(distances[c] + egressSteps);
                    row.Targets.Add(target);
                }
                result.Pawns.Add(row);
            }
            return result;
        }

        private static IntVec3 Native(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.Completeness Complete(int count) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
        private static object Encode(IMessage reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Spatial access reply exceeds one MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}
