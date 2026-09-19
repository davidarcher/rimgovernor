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
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeZoneObservationTools
    {
        private const string ToolName = "rimgovernor/observations_list_zones";
        // Grid-consistency and contiguity require a full map cell scan per zone
        // (mirroring NativeZoneRecord.Evidence()'s own phantom-cell detection);
        // bounding the page keeps that O(zones * map cells) cost small. The
        // guarded-operations CAS flow (ReadZoneEditTarget) only ever asks for
        // one zone by exact id, well under this limit.
        private const int MaxPage = 16;

        [Tool(ToolName, Title = "Read typed zones", Description = "Read exact zone identity, type, bounds and per-zone CAS snapshot tokens. Cells are included only when requested. No filter contents, stored resources, anomalies or crop-plant counts yet. changed_since_tick (unfiltered reads only) lists the zones whose row changed at or after that tick, counts the rest in unchanged and names the zones removed since in removed_ids; an ask older than 2500 ticks is STALE and needs a full read. as_of_tick is always the context tick.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListZonesReply. Unavailable replaces oversized collections; unsupported facts are explicit.", Always = true)]
        public async Task<object> ListZones(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListZonesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ListZonesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListZonesReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListZonesReply { Failure = error });
                try
                {
                    var source = map.zoneManager.AllZones.Where(z => z != null && z.Cells.Count != 0).ToList();
                    var matched = source.Where(z => Matches(z, parsed)).OrderBy(z => z.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                    var seed = QuerySeed(parsed);
                    // Entity tracking (issue #358) follows every unfiltered
                    // read: a full one primes the shadow and sweeps the
                    // removed, a changed_since one lists only the changed.
                    var tracking = Unfiltered(parsed) ? EntityTracking.For(map, ToolName + parsed.IncludeCells + parsed.IncludeContents + parsed.IncludeFilter) : null;
                    if (parsed.HasChangedSinceTick && EntityTracking.Expired(parsed.ChangedSinceTick))
                        return ProtoBoundary.Encode(new Obs.ListZonesReply { Unavailable = Unavailable(Common.UnavailableReason.Stale, "changed_since_tick is older than the tombstone window; read in full.") });
                    var rows = new Dictionary<string, Obs.ZoneState>();
                    var listed = matched;
                    var snapshot = new Obs.ZonesSnapshot { Context = context, AsOfTick = context.Tick, Unchanged = 0 };
                    if (parsed.HasChangedSinceTick)
                    {
                        listed = new List<Zone>();
                        foreach (var zone in matched)
                        {
                            var row = Project(zone, map, context, parsed);
                            if (tracking!.Note(row.Id, row) >= parsed.ChangedSinceTick) { rows[row.Id] = row; listed.Add(zone); }
                            else snapshot.Unchanged++;
                        }
                    }
                    if (tracking != null)
                    {
                        tracking.Sweep(new HashSet<string>(matched.Select(z => Id(z.GetUniqueLoadID()))));
                        if (parsed.HasChangedSinceTick) snapshot.RemovedIds.AddRange(tracking.RemovedSince(parsed.ChangedSinceTick));
                    }
                    var afterCursor = listed;
                    if (parsed.Page != null && parsed.Page.HasCursor && parsed.Page.Cursor.Length != 0)
                    {
                        if (!NativeObservationSnapshot.Cursor.TryDecode(context.Identity, seed, parsed.Page.Cursor, out var after))
                            return ProtoBoundary.Encode(new Obs.ListZonesReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, "Zone cursor is stale or does not match this query.") });
                        afterCursor = listed.Where(z => string.CompareOrdinal(Id(z.GetUniqueLoadID()), after) > 0).ToList();
                    }
                    var page = afterCursor.Take(Limit(parsed)).ToList();
                    Require(page.Count <= MaxPage, "Matched zone collection exceeds page limit; narrow filters.");
                    var truncated = afterCursor.Count > page.Count;
                    snapshot.Completeness = Complete(page.Count, source.Count - matched.Count);
                    snapshot.Completeness.Page.Complete = !truncated;
                    if (truncated) snapshot.Completeness.Page.NextCursor = NativeObservationSnapshot.Cursor.Encode(context.Identity, seed, Id(page[page.Count-1].GetUniqueLoadID()));
                    foreach (var zone in page)
                    {
                        var id = Id(zone.GetUniqueLoadID());
                        if (!rows.TryGetValue(id, out var row)) { row = Project(zone, map, context, parsed); tracking?.Note(id, row); }
                        snapshot.Zones.Add(row);
                    }
                    return Encode(new Obs.ListZonesReply { Observed = snapshot });
                }
                catch (ReadLimit errorLimit) { return ProtoBoundary.Encode(new Obs.ListZonesReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, errorLimit.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.ListZonesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Zone facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool Validate(Obs.ListZonesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, exact bounded identifiers, supported filters and page limit1..16 are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Page != null && (request.Page.HasLimit && (request.Page.Limit < 1 || request.Page.Limit > MaxPage)
                || request.Page.HasCursor && request.Page.Cursor.Length > 4096)) return false;
            if (request.Ids.Count > MaxPage || !request.Ids.All(ProtoBoundary.IsIdentifier)
                || request.Ids.Distinct(StringComparer.Ordinal).Count() != request.Ids.Count) return false;
            if (request.HasNameContains && (request.NameContains.Length == 0 || request.NameContains.Length > 256)) return false;
            if (request.Region != null && (!CellPresent(request.Region.Minimum) || !CellPresent(request.Region.Maximum)
                || request.Region.Minimum.X > request.Region.Maximum.X || request.Region.Minimum.Z > request.Region.Maximum.Z)) return false;
            if (request.HasChangedSinceTick && (request.ChangedSinceTick < 0 || !Unfiltered(request)))
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "changed_since_tick needs a non-negative tick and a read without ids, name_contains or region.");
                return false;
            }
            return true;
        }

        // Unfiltered is a read that enumerates every zone on the map, the
        // only shape whose tracker can tell a removed zone from a filtered one.
        private static bool Unfiltered(Obs.ListZonesRequest request) => request.Ids.Count == 0 && !request.HasNameContains && request.Region == null;

        private static bool Matches(Zone zone, Obs.ListZonesRequest request)
        {
            if (request.Ids.Count != 0 && !request.Ids.Contains(Id(zone.GetUniqueLoadID()))) return false;
            if (request.HasNameContains && (zone.label ?? "").IndexOf(request.NameContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (request.Region != null && !zone.Cells.Any(c => c.x >= request.Region.Minimum.X && c.x <= request.Region.Maximum.X
                && c.z >= request.Region.Minimum.Z && c.z <= request.Region.Maximum.Z)) return false;
            return true;
        }

        // Per-zone CAS token: unlike NativeZoneCreation.MapSnapshot's whole-map
        // hash (CreateZone only needs "did any zone move"), EditZoneCells and
        // DeleteZone each guard one specific zone's own cell list and
        // configuration, mirroring NativeBuildingObservationTools.Token's
        // per-entity shape.
        internal static Obs.SnapshotRef Token(Zone zone, Common.ObservationContext context) =>
            NativeObservationSnapshot.Snapshot("zone", context, Id(zone.GetUniqueLoadID()), w => {
                var cells = zone.Cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
                w.Write(cells.Length);
                foreach (var cell in cells) { w.Write(cell.x); w.Write(cell.z); }
                w.Write(zone.label ?? "");
                if (zone is Zone_Fishing fishing)
                {
                    w.Write(fishing.Allowed); w.Write((int)fishing.repeatMode); w.Write(fishing.targetPopulationPct);
                    w.Write(fishing.targetCount); w.Write(fishing.repeatCount); w.Write(fishing.pauseWhenSatisfied); w.Write(fishing.unpauseAtCount);
                }
                if (zone is Zone_Growing growing)
                {
                    var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                    w.Write(crop?.defName ?? ""); w.Write(growing.allowSow); w.Write(growing.allowCut);
                }
                else if (zone is Zone_Stockpile stockpile)
                {
                    w.Write((int)stockpile.settings.Priority);
                    NativeStockpileSettings.WriteSignature(w, stockpile.settings.filter);
                }
            });

        private static Obs.ZoneState Project(Zone zone, Map map, Common.ObservationContext context, Obs.ListZonesRequest request)
        {
            var cells = zone.Cells;
            Require(cells.Count <= 4096, "Zone cell list exceeds 4096 cells.");
            var minX = cells.Min(c => c.x); var maxX = cells.Max(c => c.x);
            var minZ = cells.Min(c => c.z); var maxZ = cells.Max(c => c.z);
            var row = new Obs.ZoneState { Id = Id(zone.GetUniqueLoadID()), Label = PlacementPreviewOperation.Diagnostic(zone.label ?? ""),
                Type = zone is Zone_Growing ? "growing" : zone is Zone_Stockpile ? "stockpile" : "unknown",
                Bounds = new Obs.Rectangle { Minimum = new Common.Cell { X = minX, Z = minZ }, Maximum = new Common.Cell { X = maxX, Z = maxZ } },
                Snapshot = Token(zone, context) };

            var ordered = cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
            var gridCells = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone).ToArray();
            var phantom = ordered.Count(c => map.zoneManager.ZoneAt(c) != zone);
            row.Consistent = phantom == 0 && gridCells.Length == ordered.Length;
            row.Contiguous = Contiguous(ordered);
            if (request.IncludeCells)
            {
                foreach (var cell in ordered) row.ListedCells.Add(new Common.Cell { X = cell.x, Z = cell.z });
                foreach (var cell in gridCells) row.GridCells.Add(new Common.Cell { X = cell.x, Z = cell.z });
                row.CellsCompleteness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)ordered.Length, Returned = (ulong)ordered.Length };
            }
            else row.Issues.Add(Issue("listed_cells", Common.UnavailableReason.NotRequested, "Cell lists are not requested."));

            if (zone is Zone_Growing growing)
            {
                var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                if (crop != null) row.CropDefName = Id(crop.defName);
                row.ExplicitlySetCrop = crop != null; row.AllowSow = growing.allowSow; row.AllowCut = growing.allowCut;
                row.Issues.Add(Issue("priority", Common.UnavailableReason.NotApplicable, "Growing zones have no storage priority."));
            }
            else if (zone is Zone_Stockpile stockpile)
            {
                row.Priority = stockpile.settings.Priority.ToString();
                row.Issues.Add(Issue("crop_def_name", Common.UnavailableReason.NotApplicable, "Stockpile zones have no crop."));
                if (request.IncludeFilter) row.Filter = NativeStockpileSettings.Project(stockpile.settings.filter);
                else row.Issues.Add(Issue("filter", Common.UnavailableReason.NotRequested, "The stockpile filter is not requested."));
            }
            else row.Issues.Add(Issue("type", Common.UnavailableReason.Unsupported, "Zone subtype is not a growing or stockpile zone."));

            if (!(zone is Zone_Stockpile)) row.Issues.Add(Issue("filter", Common.UnavailableReason.NotApplicable, "Only stockpile zones have a filter."));
            foreach (var field in new[] { "contents", "anomalies", "free_cells", "blocked_cells", "impassable_cells", "crop_plants_in_listed_cells", "crop_plants_in_grid_cells", "slot_group_cells", "haul_grid_cells" })
                row.Issues.Add(Issue(field, Common.UnavailableReason.Unsupported, "Typed fact is not implemented by this read adapter."));
            return row;
        }

        private static bool Contiguous(IntVec3[] cells)
        {
            if (cells.Length == 0) return false;
            var selected = new HashSet<IntVec3>(cells);
            var reached = new HashSet<IntVec3> { cells[0] };
            var queue = new Queue<IntVec3>(); queue.Enqueue(cells[0]);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var offset in GenAdj.CardinalDirections)
                {
                    var next = c + offset;
                    if (selected.Contains(next) && reached.Add(next)) queue.Enqueue(next);
                }
            }
            return reached.Count == selected.Count;
        }

        private static string QuerySeed(Obs.ListZonesRequest request) => string.Join("",
            request.NameContains ?? "", request.IncludeCells, request.IncludeContents, request.IncludeFilter,
            string.Join(",", request.Ids.OrderBy(s=>s,StringComparer.Ordinal)),
            request.Region == null ? "" : request.Region.Minimum.X+","+request.Region.Minimum.Z+"-"+request.Region.Maximum.X+","+request.Region.Maximum.Z);
        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ;
        private static int Limit(Obs.ListZonesRequest request) => request.Page?.HasLimit == true ? (int)request.Page.Limit : MaxPage;
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native ID unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = (ulong)filtered, Unreadable = 0 };
        internal static object Encode(Obs.ListZonesReply reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Complete zone reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool value, string detail) { if (!value) throw new ReadLimit(detail); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
