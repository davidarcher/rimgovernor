using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Native
{
    internal static class NativeZoneObservationTools
    {
        // A caller names at most this many zones by exact id (input bound).
        // Kept (docs/developers/contracts/kept-constants.md).
        private const int MaxIds = 16;

        // On the game thread: the typed core of the tool, shared with the in-process facade. A refused request
        // or a census that cannot be read completely throws.
        internal static RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot HandleListZones(RimGovernor.Brain.GameOrders.Observations.ListZonesRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!GameThread.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                throw NativeFaults.Refused(error);
            try { return Read(map, request, context); }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Zone census read failed: " + readError);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Zone facts could not be read completely: " + readError.GetType().Name + ": " + readError.Message) });
            }
        }

        // On the main thread: the zone census for a validated request, the reply its tool encodes and the section
        // the bundle carries; a census it cannot read completely throws.
        internal static RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot Read(Map map, RimGovernor.Brain.GameOrders.Observations.ListZonesRequest parsed, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var source = map.zoneManager.AllZones.Where(z => z != null && z.Cells.Count != 0).ToList();
            var matched = source.Where(z => Matches(z, parsed)).OrderBy(z => z.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            return new RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot { Context = context, Zones = [.. matched.Select(zone => Project(zone, map, people, parsed))] };
        }

        internal static bool Validate(RimGovernor.Brain.GameOrders.Observations.ListZonesRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Identity scope, exact bounded identifiers, supported filters are required.");
            if (request == null || request.Scope?.ExpectedIdentity == null) return false;
            if (request.Ids.Count > MaxIds || !request.Ids.All(GameThread.IsIdentifier)
                || request.Ids.Distinct(StringComparer.Ordinal).Count() != request.Ids.Count) return false;
            if (request.NameContains is not null && ((request.NameContains ?? "").Length == 0 || (request.NameContains ?? "").Length > 256)) return false;
            if (request.Region != null && (request.Region.Minimum.X > request.Region.Maximum.X || request.Region.Minimum.Z > request.Region.Maximum.Z)) return false;
            return true;
        }

        private static bool Matches(Zone zone, RimGovernor.Brain.GameOrders.Observations.ListZonesRequest request)
        {
            if (request.Ids.Count != 0 && !request.Ids.Contains(Id(zone.GetUniqueLoadID()))) return false;
            if (request.NameContains is not null && (zone.label ?? "").IndexOf((request.NameContains ?? ""), StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (request.Region is { Minimum: { } lo, Maximum: { } hi } && !zone.Cells.Any(c => c.x >= lo.X && c.x <= hi.X
                && c.z >= lo.Z && c.z <= hi.Z)) return false;
            return true;
        }

        private static RimGovernor.Brain.GameFacts.Observations.ZoneState Project(Zone zone, Map map, List<Pawn> people, RimGovernor.Brain.GameOrders.Observations.ListZonesRequest request)
        {
            var cells = zone.Cells;
            var id = Id(zone.GetUniqueLoadID());
            var label = PlacementPreviewOperation.Diagnostic(zone.label ?? "");
            var bounds = new RimGovernor.Brain.GameFacts.Observations.CellExtents { Minimum = new DomainCell(cells.Min(c => c.x), cells.Min(c => c.z)), Maximum = new DomainCell(cells.Max(c => c.x), cells.Max(c => c.z)) };
            bool HumanFood(ThingDef d) => d != null && d.IsNutritionGivingIngestible && !d.IsDrug
                && d.ingestible != null && (d.ingestible.foodType & (FoodTypeFlags.Corpse | FoodTypeFlags.Kibble)) == 0
                && NativeFoodPolicy.EatenByAll(people, d);
            switch (zone)
            {
                case Zone_Growing growing:
                {
                    var crop = (BridgeCommon.PrivateInstanceField(typeof(Zone_Growing), "plantDefToGrow") ?? throw new InvalidOperationException("Zone_Growing.plantDefToGrow is unavailable.")).GetValue(growing) as ThingDef;
                    if (crop?.plant == null) throw new InvalidOperationException("Native growing crop unavailable.");
                    var visible = map.AllCells.Where(c => map.zoneManager.ZoneAt(c) == zone && !c.Fogged(map)).ToList();
                    var plants = visible.Select(c => c.GetPlant(map)).Where(p => p != null && p.def == crop).ToList();
                    var product = crop.plant.harvestedThingDef;
                    return new RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing
                    {
                        Id = id, Label = label, Bounds = bounds, Crop = Id(crop.defName),
                        ZoneCells = (uint)visible.Count,
                        UnroofedCells = (uint)visible.Count(c => !c.Roofed(map)),
                        UsableCells = (uint)visible.Count(c => map.fertilityGrid.FertilityAt(c) >= crop.plant.fertilityMin),
                        PlantedCells = (uint)plants.Count,
                        FertilePlantedCells = (uint)plants.Count(p => p.GrowthRateFactor_Fertility > 0),
                        BlightedPlants = (uint)plants.Count(p => p.Blighted),
                        EdibleCrop = product != null && HumanFood(product),
                        GrowthTotal = plants.Sum(p => (double)p.Growth),
                        FertilityFactorTotal = plants.Sum(p => (double)p.GrowthRateFactor_Fertility),
                        Temperature = map.mapTemperature.OutdoorTemp,
                    };
                }
                case Zone_Stockpile stockpile:
                {
                    var filter = stockpile.GetStoreSettings()?.filter ?? throw new InvalidOperationException("Native stockpile filter unavailable.");
                    // A food stockpile is food storage wherever it stands: a perishable the colonists eat is
                    // allowed in it. Roofing it is a later upgrade.
                    return new RimGovernor.Brain.GameFacts.Observations.ZoneState.Stockpile
                    {
                        Id = id, Label = label, Bounds = bounds,
                        Priority = stockpile.settings.Priority.ToString(),
                        FoodStorage = DefDatabase<ThingDef>.AllDefsListForReading.Any(d => HumanFood(d) && d.GetCompProperties<CompProperties_Rottable>() != null && filter.Allows(d)),
                        Filter = request.IncludeFilter ? NativeStockpileSettings.Project(stockpile.settings.filter) : null,
                    };
                }
                default:
                    return new RimGovernor.Brain.GameFacts.Observations.ZoneState.Other { Id = id, Label = label, Bounds = bounds };
            }
        }

        private static string Id(string value) => GameThread.IsIdentifier(value) ? value : throw new InvalidOperationException("Native ID unavailable.");
    }
}
