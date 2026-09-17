#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeRoomObservationTools
    {
        private const string ToolName = "rimgovernor/observations_list_rooms";
        [Tool(ToolName, Title = "Read typed rooms", Description = "Complete bounded room census and exact footprint intersection filters. Defaults exclude psychologically outdoor rooms and doorways. Contents count buildings, optionally including boundary buildings. IDs are ephemeral within the current room graph; no CAS or frozen cursor.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListRoomsReply with explicit unknown stats and unrequested cells.", Always = true)]
        public async Task<object> ListRooms(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListRoomsRequest string.")] object request = null!)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ListRoomsRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListRoomsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListRoomsReply { Failure = error });
                try
                {
                    if (parsed.Region != null && (!NativeCell(parsed.Region.Minimum).InBounds(map) || !NativeCell(parsed.Region.Maximum).InBounds(map)))
                        return ProtoBoundary.Encode(new Obs.ListRoomsReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Room filter rectangle must be within the current map.") });
                    if (map.regionGrid?.AllRooms == null || map.regionAndRoomUpdater == null || map.zoneManager == null || map.mapPawns == null)
                        return ProtoBoundary.Encode(new Obs.ListRoomsReply { Unavailable = Missing(Common.UnavailableReason.NativeComponentMissing, "Room, zone or pawn census is unavailable.") });
                    // AllRooms is the raw cache; resolve pending region changes
                    // before taking a complete physical-room census.
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    if (map.regionAndRoomUpdater.AnythingToRebuild) throw new InvalidOperationException("Native room graph remains dirty.");
                    var source = map.regionGrid.AllRooms.ToList(); Require(source.Count <= 65536, "Room census exceeds 65536 entries.");
                    if (source.Any(r => r == null) || source.Select(r => r.ID).Distinct().Count() != source.Count)
                        throw new InvalidOperationException("Null or duplicate native room census entry.");
                    var physical = source.Where(HasPhysicalRegions).ToList();
                    if (physical.Any(r => r.Map != map)) throw new InvalidOperationException("Foreign native room census entry.");
                    var pawns = map.mapPawns.AllPawnsSpawned.ToList(); Require(pawns.Count <= 65536, "Pawn census exceeds 65536 entries.");
                    var pawnRooms = new Dictionary<Room, List<Pawn>>();
                    foreach (var pawn in pawns)
                    {
                        if (pawn == null || !pawn.Spawned || pawn.Map != map) throw new InvalidOperationException();
                        var room = pawn.Position.GetRoom(map);
                        if (room == null) continue; // Impassable/unregioned cells have no room.
                        if (!pawnRooms.TryGetValue(room, out var list)) pawnRooms.Add(room, list = new List<Pawn>());
                        list.Add(pawn);
                    }
                    var snapshot = new Obs.RoomsSnapshot { Context = context };
                    var filtered = source.Count - physical.Count; var walked = 0;
                    var seed = QuerySeed(parsed);
                    string? afterId = null;
                    if (parsed.Page != null && parsed.Page.HasCursor && parsed.Page.Cursor.Length != 0)
                    {
                        if (!NativeObservationSnapshot.Cursor.TryDecode(context.Identity, seed, parsed.Page.Cursor, out var after))
                            return ProtoBoundary.Encode(new Obs.ListRoomsReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, "Room cursor is stale or does not match this query.") });
                        afterId = after;
                    }
                    var limit = parsed.Page?.HasLimit == true ? (int)parsed.Page.Limit : 256;
                    string? lastId = null; var truncated = false;
                    foreach (var room in physical.OrderBy(r => r.ID))
                    {
                        if (!Selected(parsed, Id(room.ID), room.PsychologicallyOutdoors, room.IsDoorway)) { filtered++; continue; }
                        var cells = new List<IntVec3>();
                        foreach (var cell in room.Cells)
                        {
                            Require(++walked <= 262144 && cells.Count < 65536, "Room cell traversal exceeds its work bound.");
                            if (!cell.InBounds(map)) throw new InvalidOperationException();
                            cells.Add(cell);
                        }
                        if (cells.Count == 0 || cells.Count != room.CellCount || cells.Distinct().Count() != cells.Count) throw new InvalidOperationException("Incomplete room footprint.");
                        if (parsed.Region != null && !cells.Any(c => Inside(parsed.Region, c))) { filtered++; continue; }
                        if (afterId != null && string.CompareOrdinal(Id(room.ID), afterId) <= 0) continue;
                        if (snapshot.Rooms.Count >= limit) { truncated = true; continue; }
                        lastId = Id(room.ID);
                        snapshot.Rooms.Add(Project(room, map, cells, pawnRooms.TryGetValue(room, out var members) ? members : new List<Pawn>(), parsed, context));
                    }
                    Require(snapshot.Rooms.Count <= 256, "Matched rooms exceed page limit; narrow the query.");
                    snapshot.Completeness = Complete(snapshot.Rooms.Count, filtered);
                    snapshot.Completeness.Page.Complete = !truncated;
                    if (truncated && lastId != null) snapshot.Completeness.Page.NextCursor = NativeObservationSnapshot.Cursor.Encode(context.Identity, seed, lastId);
                    return Encode(new Obs.ListRoomsReply { Observed = snapshot });
                }
                catch (ReadLimit e) { return ProtoBoundary.Encode(new Obs.ListRoomsReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, e.Message) }); }
                catch (Exception readError) { return ProtoBoundary.Encode(new Obs.ListRoomsReply { Unavailable = Missing(Common.UnavailableReason.ReadFailed,
                    PlacementPreviewOperation.Diagnostic("Room census, geometry or contents could not be read completely: " + readError)) }); }
            }, cancellationToken).ConfigureAwait(false);
        }
        internal static bool Validate(Obs.ListRoomsRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Expected identity, unique room IDs, valid rectangle and page limit1..256 required; cursor must fit the caller's current filters.");
            return request?.Scope?.ExpectedIdentity != null && request.RoomIds.Count <= 256
                && request.RoomIds.All(ProtoBoundary.IsIdentifier) && request.RoomIds.Distinct(StringComparer.Ordinal).Count() == request.RoomIds.Count
                && (request.Page == null || (!request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= 256)
                    && (!request.Page.HasCursor || request.Page.Cursor.Length <= 4096))
                && (request.Region == null || CellPresent(request.Region.Minimum) && CellPresent(request.Region.Maximum)
                    && request.Region.Minimum.X <= request.Region.Maximum.X && request.Region.Minimum.Z <= request.Region.Maximum.Z);
        }
        private static string QuerySeed(Obs.ListRoomsRequest request) => string.Join("",
            request.IncludeOutdoors, request.IncludeBoundary, request.IncludeCells,
            string.Join(",", request.RoomIds.OrderBy(i => i, StringComparer.Ordinal)),
            request.Region == null ? "" : request.Region.Minimum.X+","+request.Region.Minimum.Z+"-"+request.Region.Maximum.X+","+request.Region.Maximum.Z);
        internal static bool Selected(Obs.ListRoomsRequest request, string id, bool psychologicallyOutdoors, bool doorway)
            => (request.IncludeOutdoors || !psychologicallyOutdoors && !doorway) && (request.RoomIds.Count == 0 || request.RoomIds.Contains(id));
        internal static bool HasPhysicalRegions(Room room)
        {
            if (!room.Dereferenced) return true;
            // Native AllRooms can retain an empty district with no regions.
            // It contributes no physical room, but inconsistent geometry is unknown.
            if (room.CellCount != 0 || room.Cells.Any()) throw new InvalidOperationException("Regionless room has physical cells.");
            return false;
        }
        internal static bool Inside(Obs.Rectangle rectangle, IntVec3 cell) => cell.x >= rectangle.Minimum.X && cell.x <= rectangle.Maximum.X && cell.z >= rectangle.Minimum.Z && cell.z <= rectangle.Maximum.Z;

        private static Obs.RoomState Project(Room room, Map map, List<IntVec3> cells, List<Pawn> pawns, Obs.ListRoomsRequest request, Common.ObservationContext context)
        {
            // Room owns reusable region/thing buffers. Copy before any stat, label or
            // bed projection can read those getters again. Never enumerate Zone.Cells.
            var things = room.ContainedAndAdjacentThings.ToList(); Require(things.Count <= 65536, "Room thing census exceeds65536.");
            if (things.Any(t => t == null || !t.Spawned || t.Map != map) || things.Distinct().Count() != things.Count) throw new InvalidOperationException("Invalid or duplicate room thing membership.");
            var row = new Obs.RoomState { Id = Id(room.ID), ProperRoom = room.ProperRoom, Doorway = room.IsDoorway,
                Outdoors = room.UsesOutdoorTemperature, PsychologicallyOutdoors = room.PsychologicallyOutdoors,
                TouchesMapEdge = room.TouchesMapEdge, Fogged = room.Fogged, CellCount = (uint)cells.Count };
            var roof = room.OpenRoofCount;
            if (roof < 0 || roof > cells.Count) throw new InvalidOperationException("Invalid room open roof count.");
            row.OpenRoofCount = (uint)roof;
            try { row.TemperatureC = Finite(room.Temperature); }
            catch (Exception) { row.Issues.Add(Issue("temperature_c", Common.UnavailableReason.ReadFailed, "Native room temperature unavailable.")); }
            try
            {
                var role = room.Role;
                if (role == null) throw new InvalidOperationException();
                row.Role = Name(role.defName);
                var label = room.GetRoomRoleLabel();
                if (label != null) row.Label = PlacementPreviewOperation.Diagnostic(label);
            }
            catch (Exception) { row.Issues.Add(Issue("role/label", Common.UnavailableReason.ReadFailed, "Native room role or label unavailable.")); }
            row.Extents = new Obs.Rectangle { Minimum = Cell(new IntVec3(cells.Min(c => c.x), 0, cells.Min(c => c.z))), Maximum = Cell(new IntVec3(cells.Max(c => c.x), 0, cells.Max(c => c.z))) };
            row.Center = Cell(Center(cells));
            if (request.IncludeCells)
            {
                Require(cells.Count <= 4096, "Requested room footprint exceeds4096 cells.");
                row.Cells.Add(cells.OrderBy(c => c.z).ThenBy(c => c.x).Select(Cell)); row.CellsCompleteness = Complete(cells.Count, 0);
            }
            else
            {
                row.CellsCompleteness = new Obs.Completeness { Page = new Common.PageInfo { Complete = false }, Matched = (ulong)cells.Count, Returned = 0, Unreadable = 0 };
                row.Issues.Add(Issue("cells", Common.UnavailableReason.NotRequested, "Exact cell list not requested; geometry/count are complete."));
            }
            var stockpiles = new List<Zone_Stockpile>();
            foreach (var cell in cells) if (map.zoneManager.ZoneAt(cell) is Zone_Stockpile stockpile && !stockpiles.Contains(stockpile)) stockpiles.Add(stockpile);
            Require(stockpiles.Count <= 256 && pawns.Count <= 256, "Room pawn/zone collection exceeds256.");
            row.StockpileZoneIds.Add(stockpiles.Select(z => Name(z.GetUniqueLoadID())).OrderBy(v => v, StringComparer.Ordinal));
            foreach (var stockpile in stockpiles.OrderBy(z => z.GetUniqueLoadID(), StringComparer.Ordinal))
            {
                var stockpileContents = stockpile.slotGroup?.HeldThings?.ToList() ?? new List<Thing>();
                Require(stockpileContents.Count <= 65536, "Stockpile content census exceeds65536.");
                var membership = new Obs.StockpileMembership { ZoneId = Name(stockpile.GetUniqueLoadID()) };
                var grouped = stockpileContents.GroupBy(t => Name(t.def.defName)).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
                Require(grouped.Count <= 256, "Stockpile content definitions exceed256.");
                foreach (var group in grouped) membership.Contents.Add(new Obs.ResourceStock { Definition = new Obs.DefinitionRef { DefName = group.Key }, Units = group.Sum(t => (long)t.stackCount) });
                membership.ContentsCompleteness = Complete(grouped.Count, 0);
                row.StockpileMemberships.Add(membership);
            }
            foreach (var pawn in pawns.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) row.Pawns.Add(Entity(pawn));
            var buildings = things.OfType<Building>().Where(t => request.IncludeBoundary || room.ContainsCell(t.Position)).ToList();
            var beds = buildings.OfType<Building_Bed>().ToList(); Require(beds.Count <= 256, "Room bed collection exceeds256.");
            var colonists = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            Require(colonists.Count <= 256, "Room bed-membership colonist census exceeds256.");
            foreach (var bed in beds)
            {
                row.Beds.Add(NativeBuildingObservationTools.Project(bed));
                var membership = new Obs.RoomBedMembership { Building = NativeBuildingObservationTools.Project(bed) };
                var owners = bed.OwnersForReading.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                Require(owners.Count <= 256, "Bed owner collection exceeds256.");
                membership.Owners.Add(owners.Select(Entity));
                membership.Users.Add(colonists.Where(p => p.CurrentBed() == bed).Select(Entity));
                membership.AccessibleTo.Add(colonists.Where(p => !bed.IsForbidden(p) && p.CanReach(bed, Verse.AI.PathEndMode.OnCell, Danger.None)).Select(Entity));
                row.BedMemberships.Add(membership);
            }
            var contents = buildings.GroupBy(t => Name(t.def.defName)).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            Require(contents.Count <= 256, "Room content definitions exceed256.");
            foreach (var group in contents) row.Contents.Add(new Obs.Quantity { DefName = group.Key, Units = group.LongCount() });
            row.ContentsCompleteness = Complete(contents.Count, 0);
            foreach (var definition in new[] { RoomStatDefOf.Cleanliness, RoomStatDefOf.Wealth, RoomStatDefOf.Space, RoomStatDefOf.Beauty, RoomStatDefOf.Impressiveness })
            {
                if (definition == null) { row.Issues.Add(Issue("stats", Common.UnavailableReason.NativeComponentMissing, "A standard native room stat definition is unavailable.")); continue; }
                var stat = new Obs.RoomStat { DefName = Name(definition.defName) };
                try
                {
                    var value = room.GetStat(definition); stat.Value = Finite(value);
                    var display = definition.ScoreToString(value);
                    if (display != null) stat.Display = PlacementPreviewOperation.Diagnostic(display);
                }
                catch (Exception) { stat.ClearValue(); stat.ClearDisplay(); stat.Unavailable = Missing(Common.UnavailableReason.ReadFailed, "Native room stat unavailable."); }
                row.Stats.Add(stat);
            }
            row.Snapshot = NativeObservationSnapshot.Snapshot("room", context, row.Id, w => {
                w.Write(row.CellCount); w.Write(row.OpenRoofCount); w.Write(row.Role??""); w.Write(row.Fogged);
                foreach (var quantity in row.Contents) { w.Write(quantity.DefName); w.Write(quantity.Units); }
                foreach (var membership in row.BedMemberships) { w.Write(membership.Building.Building.Id); w.Write(membership.Owners.Count); w.Write(membership.Users.Count); }
                foreach (var membership in row.StockpileMemberships) { w.Write(membership.ZoneId??""); foreach (var stock in membership.Contents) { w.Write(stock.Definition.DefName); w.Write(stock.Units); } }
            });
            return row;
        }
        internal static IntVec3 Center(IReadOnlyList<IntVec3> cells)
        {
            if (cells.Count == 0) throw new InvalidOperationException();
            var x = (long)Math.Round(cells.Average(c => (double)c.x), MidpointRounding.AwayFromZero);
            var z = (long)Math.Round(cells.Average(c => (double)c.z), MidpointRounding.AwayFromZero);
            return cells.OrderBy(c => ((long)c.x - x) * ((long)c.x - x) + ((long)c.z - z) * ((long)c.z - z)).ThenBy(c => c.z).ThenBy(c => c.x).First();
        }
        internal static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native room fact.") : value;
        internal static object Encode(Obs.ListRoomsReply reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > 1024 * 1024)
                return ProtoBoundary.Encode(new Obs.ListRoomsReply { Unavailable = Missing(Common.UnavailableReason.LimitExceeded, "Room reply exceeds1MiB.") });
            return ProtoBoundary.Encode(reply);
        }
        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ && cell.X >= 0 && cell.Z >= 0;
        private static IntVec3 NativeCell(Common.Cell cell) => new IntVec3(cell.X, 0, cell.Z);
        private static Common.Cell Cell(IntVec3 cell) => new Common.Cell { X = cell.x, Z = cell.z };
        private static Obs.EntityRef Entity(Thing thing) => new Obs.EntityRef { Id = Name(thing.GetUniqueLoadID()), DefName = Name(thing.def.defName), MapId = thing.Map.uniqueID, Position = Cell(thing.Position) };
        private static string Id(int id) => id >= 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException("Invalid room ID.");
        private static string Name(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Invalid native room identifier.");
        private static void Require(bool valid, string detail) { if (!valid) throw new ReadLimit(detail); }
        private static Common.Unavailable Missing(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Missing(reason, detail) };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        private sealed class ReadLimit : Exception { internal ReadLimit(string detail) : base(detail) { } }
    }
}
