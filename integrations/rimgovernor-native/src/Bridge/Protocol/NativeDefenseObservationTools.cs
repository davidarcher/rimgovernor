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
    // Defense-layout reads (B06c). Both are pure observations of the game's own
    // shooting and pathing models: cover fill and sight blocking are what
    // CoverUtility/GenSight consult, and edge reachability is the ordinary
    // walk-in raid approach (ground pathing to any map edge without opening
    // doors). Neither read reserves cells, previews placement or predicts
    // where a raid will actually path.
    public sealed class NativeDefenseObservationTools
    {
        internal const int MaximumSiteCells = 2048;
        internal const int MaximumLineCells = 64;

        [Tool("rimgovernor/observations_read_defense_site", Title = "Read defense site census",
            Description = "Official DefenseSiteRequest ProtoJSON. Inclusive rectangle of at most 2048 cells: terrain, traversal, native cover fill, sight blocking, edifice ownership, natural rock, doors, home area and ground reachability to a map edge without opening doors. Unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations DefenseSiteReply ProtoJSON.", Always = true)]
        public async Task<object> ReadDefenseSite(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a DefenseSiteRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_defense_site", request!, Obs.DefenseSiteRequest.Parser, out var parsed, out var failure)
                || !ValidateSite(parsed, out failure)) return ProtoBoundary.Encode(new Obs.DefenseSiteReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity!, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.DefenseSiteReply { Failure = failure });
                try {
                    var cells = Region(parsed.Region);
                    if (cells.Any(c => !c.InBounds(map))) return ProtoBoundary.Encode(new Obs.DefenseSiteReply {
                        Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Region is outside the current map.") });
                    return Encode(new Obs.DefenseSiteReply { Observed = Site(map, parsed.Region, cells, context) });
                }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.DefenseSiteReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.DefenseSiteReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native defense site facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("rimgovernor/observations_read_lines_of_fire", Title = "Read lines of fire",
            Description = "Official LinesOfFireRequest ProtoJSON. Native line of sight and CoverUtility block chance for every (firing, approach) cell pair; at most 64 cells on each side. Unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations LinesOfFireReply ProtoJSON.", Always = true)]
        public async Task<object> ReadLinesOfFire(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a LinesOfFireRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_lines_of_fire", request!, Obs.LinesOfFireRequest.Parser, out var parsed, out var failure)
                || !ValidateLines(parsed, out failure)) return ProtoBoundary.Encode(new Obs.LinesOfFireReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                var map = Find.CurrentMap;
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity!, map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.LinesOfFireReply { Failure = failure });
                try {
                    var firing = parsed.FiringCells.Select(Native).ToList(); var approach = parsed.ApproachCells.Select(Native).ToList();
                    if (firing.Concat(approach).Any(c => !c.InBounds(map))) return ProtoBoundary.Encode(new Obs.LinesOfFireReply {
                        Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cell is outside the current map.") });
                    return Encode(new Obs.LinesOfFireReply { Observed = Lines(map, firing, approach, context) });
                }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.LinesOfFireReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.LinesOfFireReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native lines of fire could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool ValidateSite(Obs.DefenseSiteRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and an inclusive rectangle of 1..2048 cells are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            try { Region(request.Region); return true; } catch (Exception) { return false; }
        }
        internal static bool ValidateLines(Obs.LinesOfFireRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and 1..64 unique firing and approach cells are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            return Unique(request.FiringCells) && Unique(request.ApproachCells);
        }
        private static bool Unique(IEnumerable<Common.Cell> cells)
        {
            var list = cells.ToList();
            if (list.Count < 1 || list.Count > MaximumLineCells || list.Any(c => !HasCell(c))) return false;
            return list.Select(Native).Distinct().Count() == list.Count;
        }
        private static bool HasCell(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ;
        private static IntVec3 Native(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        internal static List<IntVec3> Region(Obs.Rectangle? region)
        {
            var min = region?.Minimum; var max = region?.Maximum;
            if (!HasCell(min) || !HasCell(max) || max!.X < min!.X || max.Z < min.Z) throw new ArgumentException("Invalid rectangle.");
            var width = (long)max.X - min.X + 1; var height = (long)max.Z - min.Z + 1;
            if (width * height > MaximumSiteCells) throw new ReadLimit("Region exceeds the 2048-cell defense site bound.");
            var result = new List<IntVec3>((int)(width * height));
            for (long z = min.Z; z <= max.Z; z++) for (long x = min.X; x <= max.X; x++) result.Add(new IntVec3((int)x, 0, (int)z));
            return result;
        }

        private static Obs.DefenseSiteSnapshot Site(Map map, Obs.Rectangle region, List<IntVec3> cells, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            // Hostile walk-in raiders path on the ground and treat closed doors
            // as obstacles they must bash; reachability without opening doors is
            // the conservative "can arrive here from the edge" fact.
            var raider = TraverseParms.For(TraverseMode.NoPassClosedDoors, Danger.Deadly);
            var snapshot = new Obs.DefenseSiteSnapshot { Context = context, Region = region.Clone(),
                MapSize = new Obs.MapSize { Width = checked((uint)map.Size.x), Height = checked((uint)map.Size.z) } };
            foreach (var cell in cells) {
                var row = new Obs.DefenseCell { Cell = new Common.Cell { X = cell.x, Z = cell.z }, Fogged = cell.Fogged(map) };
                if (row.Fogged) { row.Issues.Add(Issue("terrain", Common.UnavailableReason.NotApplicable, "Fogged cell geometry is unknown.")); snapshot.Cells.Add(row); continue; }
                row.Terrain = Identifier(cell.GetTerrain(map)?.defName);
                row.Walkable = cell.Walkable(map); row.Passable = !cell.Impassable(map);
                row.HomeArea = map.areaManager.Home[cell];
                row.BlocksSight = !cell.CanBeSeenOver(map);
                var edifice = cell.GetEdifice(map);
                var cover = cell.GetCover(map);
                row.CoverFill = Finite(cover?.def.fillPercent ?? edifice?.def.fillPercent ?? 0);
                if (edifice != null) {
                    row.EdificeDefName = Identifier(edifice.def.defName);
                    row.PlayerOwned = edifice.Faction == player;
                    row.NaturalRock = edifice.def.building?.isNaturalRock == true || edifice.def.mineable;
                    row.Door = edifice is Building_Door;
                } else { row.PlayerOwned = false; row.NaturalRock = false; row.Door = false; }
                row.EdgeReachable = row.Passable && map.reachability.CanReachMapEdge(cell, raider);
                snapshot.Cells.Add(row);
            }
            snapshot.Completeness = Complete(snapshot.Cells.Count);
            return snapshot;
        }

        private static Obs.LinesOfFireSnapshot Lines(Map map, List<IntVec3> firing, List<IntVec3> approach, Common.ObservationContext context)
        {
            var snapshot = new Obs.LinesOfFireSnapshot { Context = context };
            foreach (var from in firing) foreach (var to in approach) {
                var row = new Obs.LineOfFire { From = new Common.Cell { X = from.x, Z = from.z }, To = new Common.Cell { X = to.x, Z = to.z },
                    Distance = Finite((from - to).LengthHorizontal) };
                if (from.Fogged(map) || to.Fogged(map)) { row.Issues.Add(Issue("line_of_sight", Common.UnavailableReason.NotApplicable, "Fogged endpoint geometry is unknown.")); snapshot.Lines.Add(row); continue; }
                row.LineOfSight = GenSight.LineOfSight(from, to, map, true);
                row.TargetCover = Finite(CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(to), from, map));
                row.ShooterCover = Finite(CoverUtility.CalculateOverallBlockChance(new LocalTargetInfo(from), to, map));
                snapshot.Lines.Add(row);
            }
            snapshot.Completeness = Complete(snapshot.Lines.Count);
            return snapshot;
        }

        private static string Identifier(string? value) => ProtoBoundary.IsIdentifier(value!) ? value! : throw new InvalidOperationException("Native identifier unavailable.");
        private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
        private static object Encode(IMessage reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Defense reply exceeds one MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}
