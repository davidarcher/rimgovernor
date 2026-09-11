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

namespace HomeBridge.BridgeTools
{
    public sealed class SpatialAccessTools
    {
        private static HashSet<IntVec3> Parse(string value, Map map, int limit)
        {
            var cells = new HashSet<IntVec3>();
            foreach (var part in (value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split(',');
                int x, z;
                if (pair.Length != 2 || !int.TryParse(pair[0], out x) || !int.TryParse(pair[1], out z))
                    throw new ArgumentException("Expected exact x,z cells separated by semicolons");
                var cell = new IntVec3(x, 0, z);
                if (!cell.InBounds(map) || !cells.Add(cell) || cells.Count > limit)
                    throw new ArgumentException("Duplicate, out-of-map or excessive spatial cells");
            }
            return cells;
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
                foreach (var next in new[] { cell + IntVec3.North, cell + IntVec3.South,
                                             cell + IntVec3.East, cell + IntVec3.West })
                    if (allowed.Contains(next) && found.Add(next)) { distance[next] = distance[cell] + 1; queue.Enqueue(next); }
            }
            return found;
        }

        [Tool("home/spatial_access", Title = "Inspect projected colony access",
            Description = "Read-only paused-map access audit. Compare each mobile colonist's current safe, unfogged, allowed-area four-neighbor component with projected impassable cells; preserve every previously reachable unoccupied cell. Reports native CanReach for exact target cells separately. No orders, path-grid edits or simulated construction. Doors use native pawn opening eligibility. Refuses maps above 262144 cells or more than 32 mobile colonists.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Exact native-preview completed impassable footprints, x,z;x,z; doors omitted", DefaultValue = "")] string blockedCells = "",
            [ToolParameter(Description = "Exact access targets, x,z;x,z, at most 128", DefaultValue = "")] string targetCells = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var timer = Stopwatch.StartNew();
                var map = Find.CurrentMap;
                if (map == null || Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
                    return new { success = false, error = "Paused map required for spatial access" };
                if ((long)map.Size.x * map.Size.z > 262144)
                    return new { success = false, error = "Map exceeds bounded spatial audit size" };
                try
                {
                    var blocked = Parse(blockedCells, map, 16384);
                    var targets = Parse(targetCells, map, 128);
                    var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.InMentalState)
                        .OrderBy(p => p.thingIDNumber).ToList();
                    if (pawns.Count == 0 || pawns.Count > 32)
                        return new { success = false, error = "Require one through 32 mobile native colonists" };
                    var terrain = map.AllCells.Where(c => !c.Fogged(map) && c.Walkable(map)).ToList();
                    var rows = new List<object>();
                    var accepted = true;
                    var targetAccess = targets.ToDictionary(c => c, c => false);
                    foreach (var pawn in pawns)
                    {
                        var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                        var allowed = new HashSet<IntVec3>(terrain.Where(c => (area == null || area[c])
                            && c.GetDangerFor(pawn, map) == Danger.None
                            && (!(c.GetEdifice(map) is Building_Door door) || door.PawnCanOpen(pawn))));
                        var before = Reach(pawn.Position, allowed, out var ignored);
                        allowed.ExceptWith(blocked);
                        var origin = pawn.Position;
                        var egressSteps = 0;
                        var pawnBlocked = blocked.Contains(origin);
                        if (pawnBlocked)
                        {
                            // Ordinary construction can move a pawn aside. Require a
                            // currently safe native exit, without moving the pawn here.
                            var footprint = Reach(origin, new HashSet<IntVec3>(before.Where(blocked.Contains)), out var exits);
                            var frontier = footprint.SelectMany(c => new[] { c + IntVec3.North, c + IntVec3.South,
                                c + IntVec3.East, c + IntVec3.West }).Where(allowed.Contains).Distinct()
                                .OrderBy(c => c.DistanceToSquared(origin)).ThenBy(c => c.x).ThenBy(c => c.z).ToList();
                            origin = frontier.Where(c => pawn.CanReach(c, PathEndMode.OnCell, Danger.None))
                                .Select(c => (IntVec3?)c).FirstOrDefault() ?? IntVec3.Invalid;
                            if (origin.IsValid)
                                egressSteps = exits.Where(pair => Math.Abs(pair.Key.x-origin.x)+Math.Abs(pair.Key.z-origin.z)==1)
                                    .Min(pair => pair.Value)+1;
                        }
                        var after = Reach(origin, allowed, out var distances);
                        var lost = before.Where(c => !blocked.Contains(c) && !after.Contains(c)).ToList();
                        if (!origin.IsValid || lost.Count != 0) accepted = false;
                        var reach = targets.OrderBy(c => c.x).ThenBy(c => c.z).Select(c => {
                            var native = pawn.CanReach(c, PathEndMode.OnCell, Danger.None);
                            var projected = after.Contains(c);
                            if (native && projected) targetAccess[c] = true;
                            return new { x = c.x, z = c.z, nativeReachable = native, projectedReachable = projected,
                                projectedSteps = projected ? (int?)(distances[c]+egressSteps) : null };
                        }).ToList();
                        rows.Add(new { pawn = pawn.ThingID, position = BridgeCommon.Pos(pawn.Position),
                            currentCells = before.Count, projectedCells = after.Count,
                            lostCellCount = lost.Count, occupiedByProjection = pawnBlocked,
                            projectedOrigin = origin.IsValid ? BridgeCommon.Pos(origin) : null, egressSteps,
                            lostCells = lost.OrderBy(c => c.x).ThenBy(c => c.z).Take(16).Select(BridgeCommon.Pos).ToList(),
                            targets = reach });
                    }
                    if (targetAccess.Values.Any(value => !value)) accepted = false;
                    return new { success = true, accepted, mapId = map.uniqueID, tick = Find.TickManager.TicksGame,
                        mapCells = map.Size.x * map.Size.z, observedWalkableCells = terrain.Count,
                        pawnCount = pawns.Count, projectedBlockedCells = blocked.Count, targets = targets.Count,
                        pawns = rows, elapsedMilliseconds = timer.ElapsedMilliseconds,
                        scope = "Current native reachability and conservative four-neighbor projection; not a forecast of pawn labor, door locks, future danger or simulation changes" };
                }
                catch (Exception error) { return new { success = false, error = error.Message }; }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
