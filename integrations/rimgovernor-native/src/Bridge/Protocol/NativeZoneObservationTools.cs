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
        // A caller names at most this many zones by exact id (input bound).
        private const int MaxIds = 16;

        [Tool(ToolName, Title = "Read typed zones", Description = "Read exact zone identity, type, bounds and per-zone CAS snapshot tokens. Cells are included only when requested. No filter contents, stored resources, anomalies or crop-plant counts yet.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListZonesReply. Unsupported facts are explicit.", Always = true)]
        public async Task<object> ListZones(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListZonesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ListZonesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListZonesReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListZonesReply { Failure = error });
                return ProtoBoundary.Encode(Read(map, parsed, context));
            }, cancellationToken).ConfigureAwait(false);
        }

        // Read is the read on the main thread under a validated identity: the
        // reply its tool encodes, and the section the bundle carries (#593).
        internal static Obs.ListZonesReply Read(Map map, Obs.ListZonesRequest parsed, Common.ObservationContext context)
        {
            try
            {
                var source = map.zoneManager.AllZones.Where(z => z != null && z.Cells.Count != 0).ToList();
                var matched = source.Where(z => Matches(z, parsed)).OrderBy(z => z.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                var filtered = source.Count - matched.Count;
                var snapshot = new Obs.ZonesSnapshot { Context = context, Completeness = Complete(matched.Count, filtered),
                    MapSnapshot = NativeZoneCreation.MapSnapshot(map, context) };
                foreach (var zone in matched) snapshot.Zones.Add(Project(zone, map, context, parsed));
                return new Obs.ListZonesReply { Observed = snapshot };
            }
            catch (Exception) { return new Obs.ListZonesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Zone facts could not be read completely.") }; }
        }

        internal static bool Validate(Obs.ListZonesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, exact bounded identifiers, supported filters are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Ids.Count > MaxIds || !request.Ids.All(ProtoBoundary.IsIdentifier)
                || request.Ids.Distinct(StringComparer.Ordinal).Count() != request.Ids.Count) return false;
            if (request.HasNameContains && (request.NameContains.Length == 0 || request.NameContains.Length > 256)) return false;
            if (request.Region != null && (!CellPresent(request.Region.Minimum) || !CellPresent(request.Region.Maximum)
                || request.Region.Minimum.X > request.Region.Maximum.X || request.Region.Minimum.Z > request.Region.Maximum.Z)) return false;
            return true;
        }

        private static bool Matches(Zone zone, Obs.ListZonesRequest request)
        {
            if (request.Ids.Count != 0 && !request.Ids.Contains(Id(zone.GetUniqueLoadID()))) return false;
            if (request.HasNameContains && (zone.label ?? "").IndexOf(request.NameContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (request.Region != null && !zone.Cells.Any(c => c.x >= request.Region.Minimum.X && c.x <= request.Region.Maximum.X
                && c.z >= request.Region.Minimum.Z && c.z <= request.Region.Maximum.Z)) return false;
            return true;
        }

        // Per-zone snapshot token over one zone's own cell list and
        // configuration, mirroring NativeBuildingObservationTools.Token's
        // per-entity shape. The zone intents send none: native validates them
        // against the live zone at apply (#941).
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
            var minX = cells.Min(c => c.x); var maxX = cells.Max(c => c.x);
            var minZ = cells.Min(c => c.z); var maxZ = cells.Max(c => c.z);
            var row = new Obs.ZoneState { Id = Id(zone.GetUniqueLoadID()), Label = PlacementPreviewOperation.Diagnostic(zone.label ?? ""),
                Type = zone is Zone_Growing ? "growing" : zone is Zone_Stockpile ? "stockpile" : "unknown",
                Bounds = new Obs.Rectangle { Minimum = new Common.Cell { X = minX, Z = minZ }, Maximum = new Common.Cell { X = maxX, Z = maxZ } },
                Snapshot = Token(zone, context) };
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            Func<ThingDef, bool> humanFood = d => d != null && d.IsNutritionGivingIngestible && !d.IsDrug
                && d.ingestible != null && (d.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                && people.All(p => p.WillEat(d));
            row.FoodStorage = zone is Zone_Stockpile storage && storage.GetStoreSettings()?.filter != null
                && DefDatabase<ThingDef>.AllDefsListForReading.Any(d => humanFood(d) && storage.GetStoreSettings().filter.Allows(d))
                && map.AllCells.Count(c => map.zoneManager.ZoneAt(c) == zone && c.Roofed(map) && CellTracking.Indoors(c.GetRoom(map))) >= 9;

            var ordered = cells.OrderBy(c => c.x).ThenBy(c => c.z).ToArray();
            var gridCells = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone).ToArray();
            var phantom = ordered.Count(c => map.zoneManager.ZoneAt(c) != zone);
            row.Consistent = phantom == 0 && gridCells.Length == ordered.Length;
            row.Contiguous = Contiguous(ordered);
            if (request.IncludeCells)
            {
                foreach (var cell in ordered) row.ListedCells.Add(new Common.Cell { X = cell.x, Z = cell.z });
                foreach (var cell in gridCells) row.GridCells.Add(new Common.Cell { X = cell.x, Z = cell.z });
            }
            else row.Issues.Add(Issue("listed_cells", Common.UnavailableReason.NotRequested, "Cell lists are not requested."));

            if (zone is Zone_Growing growing)
            {
                var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                if (crop != null) row.CropDefName = Id(crop.defName);
                if (crop?.plant == null) throw new InvalidOperationException("Native growing crop unavailable.");
                var visible = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone && !c.Fogged(map)).ToList();
                var plants = visible.Select(c => c.GetPlant(map)).Where(p => p != null && p.def == crop).ToList();
                var product = crop.plant.harvestedThingDef;
                var edible = product != null && humanFood(product);
                row.Farm = new Obs.FarmFacts { ZoneId = row.Id, Crop = crop.defName,
                    UsableCells = (uint)visible.Count(c => map.fertilityGrid.FertilityAt(c) >= crop.plant.fertilityMin),
                    PlantedCells = (uint)plants.Count, GrowingCells = (uint)plants.Count(p => p.GrowthRateFactor_Temperature > 0 && p.GrowthRateFactor_Fertility > 0),
                    EdibleCrop = edible, NutritionPerHarvestCell = edible ? crop.plant.harvestYield * product.GetStatValueAbstract(StatDefOf.Nutrition) : 0 };
                if (plants.Count > 0) row.Farm.HarvestLowerBoundDays = plants.Min(p => (1f - p.Growth) * crop.plant.growDays / Math.Max(.01f, p.GrowthRateFactor_Fertility));
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

        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.HasX && cell.HasZ;
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native ID unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Filtered = (ulong)filtered };
    }
}
