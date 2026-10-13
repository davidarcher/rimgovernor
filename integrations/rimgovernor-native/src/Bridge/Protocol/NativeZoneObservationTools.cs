#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeZoneObservationTools
    {
        private const string ToolName = "rimgovernor/observations_list_zones";
        // A caller names at most this many zones by exact id (input bound).
        // Kept (docs/developers/contracts/kept-constants.md).
        private const int MaxIds = 16;

        [Tool(ToolName, Title = "Read typed zones", Description = "Read exact zone identity, type, bounds and per-zone CAS snapshot tokens. Cells are included only when requested. No filter contents, stored resources, anomalies or crop-plant counts yet.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ListZonesReply. Unsupported facts are explicit.", Always = true)]
        public async Task<object> ListZones(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ListZonesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, out Obs.ListZonesRequest? parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.ListZonesReply { Outcome = new Obs.ListZonesReplyOutcome.FailureCase(failure) });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ListZonesReply { Outcome = error });
                return ProtoBoundary.Encode(Read(map, parsed, context));
            }, cancellationToken).ConfigureAwait(false);
        }

        // Read is the read on the main thread under a validated identity: the
        // reply its tool encodes, and the section the bundle carries.
        internal static Obs.ListZonesReply Read(Map map, Obs.ListZonesRequest parsed, Common.ObservationContext context)
        {
            try
            {
                var source = map.zoneManager.AllZones.Where(z => z != null && z.Cells.Count != 0).ToList();
                var matched = source.Where(z => Matches(z, parsed)).OrderBy(z => z.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
                var filtered = source.Count - matched.Count;
                var snapshot = new Obs.ZonesSnapshot { Context = context, Completeness = Complete(matched.Count, filtered) };
                foreach (var zone in matched) snapshot = snapshot with { Zones = [.. snapshot.Zones, Project(zone, map, context, parsed)] };
                return new Obs.ListZonesReply { Outcome = snapshot };
            }
            catch (Exception) { return new Obs.ListZonesReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Zone facts could not be read completely.") }; }
        }

        internal static bool Validate(Obs.ListZonesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity scope, exact bounded identifiers, supported filters are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Ids.Count > MaxIds || !request.Ids.All(ProtoBoundary.IsIdentifier)
                || request.Ids.Distinct(StringComparer.Ordinal).Count() != request.Ids.Count) return false;
            if (request.NameContains is not null && ((request.NameContains ?? "").Length == 0 || (request.NameContains ?? "").Length > 256)) return false;
            if (request.Region != null && (request.Region.Minimum is not { } lo || request.Region.Maximum is not { } hi || !CellPresent(lo) || !CellPresent(hi)
                || lo.X.GetValueOrDefault() > hi.X.GetValueOrDefault() || lo.Z.GetValueOrDefault() > hi.Z.GetValueOrDefault())) return false;
            return true;
        }

        private static bool Matches(Zone zone, Obs.ListZonesRequest request)
        {
            if (request.Ids.Count != 0 && !request.Ids.Contains(Id(zone.GetUniqueLoadID()))) return false;
            if (request.NameContains is not null && (zone.label ?? "").IndexOf((request.NameContains ?? ""), StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (request.Region is { Minimum: { } lo, Maximum: { } hi } && !zone.Cells.Any(c => c.x >= lo.X.GetValueOrDefault() && c.x <= hi.X.GetValueOrDefault()
                && c.z >= lo.Z.GetValueOrDefault() && c.z <= hi.Z.GetValueOrDefault())) return false;
            return true;
        }

        private static Obs.ZoneState Project(Zone zone, Map map, Common.ObservationContext context, Obs.ListZonesRequest request)
        {
            var cells = zone.Cells;
            var minX = cells.Min(c => c.x); var maxX = cells.Max(c => c.x);
            var minZ = cells.Min(c => c.z); var maxZ = cells.Max(c => c.z);
            var row = new Obs.ZoneState { Id = Id(zone.GetUniqueLoadID()), Label = PlacementPreviewOperation.Diagnostic(zone.label ?? ""),
                Type = zone is Zone_Growing ? "growing" : zone is Zone_Stockpile ? "stockpile" : "unknown",
                Bounds = new Obs.Rectangle { Minimum = new Common.Cell { X = minX, Z = minZ }, Maximum = new Common.Cell { X = maxX, Z = maxZ } } };
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            Func<ThingDef, bool> humanFood = d => d != null && d.IsNutritionGivingIngestible && !d.IsDrug
                && d.ingestible != null && (d.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                && NativeFoodPolicy.EatenByAll(people, d);
            // A food stockpile is food storage wherever it stands: a perishable the
            // colonists eat is allowed in it. Roofing it is a later upgrade.
            row = row with { FoodStorage = zone is Zone_Stockpile storage && storage.GetStoreSettings()?.filter != null
                && DefDatabase<ThingDef>.AllDefsListForReading.Any(d => humanFood(d) && d.GetCompProperties<CompProperties_Rottable>() != null && storage.GetStoreSettings().filter.Allows(d)) };

            if (zone is Zone_Growing growing)
            {
                var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                if (crop != null) row = row with { CropDefName = Id(crop.defName) };
                if (crop?.plant == null) throw new InvalidOperationException("Native growing crop unavailable.");
                var visible = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone && !c.Fogged(map)).ToList();
                var plants = visible.Select(c => c.GetPlant(map)).Where(p => p != null && p.def == crop).ToList();
                var product = crop.plant.harvestedThingDef;
                var edible = product != null && humanFood(product);
                var farm = new Obs.FarmFacts { Zone = NativeRef.Of(row.Id), Crop = crop.defName,
                    UsableCells = (uint)visible.Count(c => map.fertilityGrid.FertilityAt(c) >= crop.plant.fertilityMin),
                    PlantedCells = (uint)plants.Count, EdibleCrop = edible,
                    Temperature = map.mapTemperature.OutdoorTemp, FertilePlantedCells = (uint)plants.Count(p => p.GrowthRateFactor_Fertility > 0),
                    BlightedPlants = (uint)plants.Count(p => p.Blighted) };
                if (plants.Count > 0)
                    farm = farm with {
                        GrowthMin = plants.Min(p => p.Growth), GrowthMean = plants.Average(p => p.Growth),
                        FertilityFactorMin = plants.Min(p => p.GrowthRateFactor_Fertility), FertilityFactorMean = plants.Average(p => p.GrowthRateFactor_Fertility),
                        LightFactorMean = plants.Average(p => p.GrowthRateFactor_Light) };
                row = row with { Farm = farm };
                row = row with { ExplicitlySetCrop = crop != null }; row = row with { AllowSow = growing.allowSow }; row = row with { AllowCut = growing.allowCut };
                row = row with { Issues = [.. row.Issues, Issue("priority", Common.UnavailableReason.NotApplicable, "Growing zones have no storage priority.")] };
            }
            else if (zone is Zone_Stockpile stockpile)
            {
                row = row with { Priority = stockpile.settings.Priority.ToString() };
                row = row with { Issues = [.. row.Issues, Issue("crop_def_name", Common.UnavailableReason.NotApplicable, "Stockpile zones have no crop.")] };
                if (request.IncludeFilter.GetValueOrDefault()) row = row with { Filter = NativeStockpileSettings.Project(stockpile.settings.filter) };
                else row = row with { Issues = [.. row.Issues, Issue("filter", Common.UnavailableReason.NotRequested, "The stockpile filter is not requested.")] };
            }
            else row = row with { Issues = [.. row.Issues, Issue("type", Common.UnavailableReason.Unsupported, "Zone subtype is not a growing or stockpile zone.")] };

            if (!(zone is Zone_Stockpile)) row = row with { Issues = [.. row.Issues, Issue("filter", Common.UnavailableReason.NotApplicable, "Only stockpile zones have a filter.")] };
            foreach (var field in new[] { "contents", "anomalies" })
                row = row with { Issues = [.. row.Issues, Issue(field, Common.UnavailableReason.Unsupported, "Typed fact is not implemented by this read adapter.")] };
            return row;
        }

        private static bool CellPresent(Common.Cell? cell) => cell != null && cell.X is not null && cell.Z is not null;
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native ID unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.ReadIssue Issue(string field, Common.UnavailableReason reason, string detail) => new Obs.ReadIssue { Field = field, Unavailable = Unavailable(reason, detail) };
        private static Obs.Completeness Complete(int count, int filtered) => new Obs.Completeness { Filtered = (ulong)filtered };
    }
}
