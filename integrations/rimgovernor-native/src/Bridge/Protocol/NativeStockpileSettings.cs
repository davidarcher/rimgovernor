#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using FilterBase = RimGovernor.Brain.Domain.FilterBase;
using Quality = RimGovernor.Brain.Domain.Quality;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The typed Operations.StockpileSettings body shared by the zone intent's create and
    /// PatchStockpile: validation, resolution of every selector against the
    /// def database, application to a live or scratch ThingFilter, exact
    /// live-versus-desired comparison and the ZoneState.filter projection.
    /// A preset is applied first, then a replacement list (disallow all,
    /// allow the list), then allow, then disallow, then the ranges -- the
    /// order the storage tab's own buttons compose in. Every selector must resolve
    /// exactly (defName, never label) or the whole body is refused.
    /// </summary>
    internal static class NativeStockpileSettings
    {
        internal sealed class Resolved
        {
            internal RimWorld.StoragePriority? Priority;
            internal string? Preset;
            internal StockpileFilter.Resolved? Replace;
            internal readonly StockpileFilter.Resolved Allow = new StockpileFilter.Resolved();
            internal readonly StockpileFilter.Resolved Disallow = new StockpileFilter.Resolved();
            internal FloatRange? HitPoints;
            internal QualityRange? Quality;
        }

        internal static string PresetName(FilterBase preset) => preset switch
        {
            FilterBase.Everything => "everything",
            FilterBase.Nothing => "nothing",
            FilterBase.Food => "food",
            FilterBase.Perishables => "perishables",
            FilterBase.Nonperishables => "nonperishables",
            FilterBase.OutdoorSafe => "outdoorSafe",
            FilterBase.IndoorOnly => "indoorOnly",
            FilterBase.OutdoorSafeWaste => "outdoorSafeWaste",
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown filter preset."),
        };

        private static Quality ToQuality(QualityCategory q) => q switch
        {
            QualityCategory.Awful => Quality.Awful,
            QualityCategory.Poor => Quality.Poor,
            QualityCategory.Normal => Quality.Normal,
            QualityCategory.Good => Quality.Good,
            QualityCategory.Excellent => Quality.Excellent,
            QualityCategory.Masterwork => Quality.Masterwork,
            QualityCategory.Legendary => Quality.Legendary,
            _ => throw new ArgumentOutOfRangeException(nameof(q), q, "Unknown quality category."),
        };
        /// <summary>Structural validation without the def database: shapes,
        /// bounds and range coherence. Resolve does the database lookups.</summary>
        internal static bool Valid(Operations.StockpileSettings? settings)
        {
            if (settings == null || settings.Priority is null || settings.Preset is null) return false;
            if (settings.Priority is Operations.StockpilePriorityChange.Selected selectedPriority && !Enum.IsDefined(selectedPriority.Value)
                || settings.Preset is Operations.StockpilePresetChange.Selected selectedPreset && !Enum.IsDefined(selectedPreset.Value)) return false;
            var filter = settings.Filter;
            if (filter == null) return settings.Priority is Operations.StockpilePriorityChange.Selected || settings.Preset is Operations.StockpilePresetChange.Selected;
            var selectors = filter.Allow.Concat(filter.Disallow).Concat(filter.Replace?.Selectors ?? Enumerable.Empty<Operations.FilterSelector>()).ToList();
            if (selectors.Any(s => !ValidSelector(s))) return false;
            if (filter.HitPoints is null || (filter.QualityMin is not null) != (filter.QualityMax is not null)) return false;
            if (filter.HitPoints is Operations.HitPointRange.Selected range && (double.IsNaN(range.Minimum) || double.IsNaN(range.Maximum)
                || range.Minimum < 0 || range.Maximum > 1 || range.Minimum > range.Maximum)) return false;
            if (filter.QualityMin is not null && (ParseQuality((filter.QualityMin ?? "")) == null || ParseQuality((filter.QualityMax ?? "")) == null
                || ParseQuality((filter.QualityMin ?? ""))!.Value > ParseQuality((filter.QualityMax ?? ""))!.Value)) return false;
            return true;
        }

        private static bool ValidSelector(Operations.FilterSelector selector)
        {
            switch (selector.Definition)
            {
                case Operations.FilterSelectorDefinition.ThingDefCase: return ProtoBoundary.IsIdentifier(selector.Definition?.ThingDef);
                case Operations.FilterSelectorDefinition.CategoryDefCase: return ProtoBoundary.IsIdentifier(selector.Definition?.CategoryDef);
                case Operations.FilterSelectorDefinition.SpecialFilterDefCase: return ProtoBoundary.IsIdentifier(selector.Definition?.SpecialFilterDef);
                default: return false;
            }
        }

        internal static QualityCategory? ParseQuality(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Enum.TryParse<QualityCategory>(name, false, out var quality) && Enum.IsDefined(typeof(QualityCategory), quality) ? quality : (QualityCategory?)null;
        }

        /// <summary>Resolves every selector exactly by defName. Special filters
        /// must be configurable ones (the storage tab's own checkboxes); a
        /// ThingDef must be storable in the given universe.</summary>
        internal static Resolved? Resolve(Operations.StockpileSettings? settings, List<ThingDef> universe)
        {
            if (settings is null || !Valid(settings)) return null;
            var result = new Resolved();
            if (settings.Priority is Operations.StockpilePriorityChange.Selected { Value: var priority }) result.Priority = NativeZoneCreation.ToNativePriority(priority);
            if (settings.Preset is Operations.StockpilePresetChange.Selected { Value: var preset }) result.Preset = PresetName(preset);
            var filter = settings.Filter;
            if (filter == null) return result;
            var storable = new HashSet<ThingDef>(universe);
            if (filter.Replace != null)
            {
                result.Replace = new StockpileFilter.Resolved();
                if (!ResolveAll(filter.Replace.Selectors, storable, result.Replace)) return null;
            }
            if (!ResolveAll(filter.Allow, storable, result.Allow) || !ResolveAll(filter.Disallow, storable, result.Disallow)) return null;
            if (filter.HitPoints is Operations.HitPointRange.Selected range) result.HitPoints = new FloatRange((float)range.Minimum, (float)range.Maximum);
            if (filter.QualityMin is not null) result.Quality = new QualityRange(ParseQuality((filter.QualityMin ?? ""))!.Value, ParseQuality((filter.QualityMax ?? ""))!.Value);
            return result;
        }

        private static bool ResolveAll(IEnumerable<Operations.FilterSelector> selectors, HashSet<ThingDef> storable, StockpileFilter.Resolved into)
        {
            foreach (var selector in selectors)
            {
                switch (selector.Definition)
                {
                    case Operations.FilterSelectorDefinition.ThingDefCase:
                        var def = DefDatabase<ThingDef>.GetNamedSilentFail(selector.Definition?.ThingDef);
                        if (def == null || !storable.Contains(def)) return false;
                        into.Defs.Add(def); break;
                    case Operations.FilterSelectorDefinition.CategoryDefCase:
                        var category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(selector.Definition?.CategoryDef);
                        if (category == null) return false;
                        into.Categories.Add(category); break;
                    case Operations.FilterSelectorDefinition.SpecialFilterDefCase:
                        var special = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(selector.Definition?.SpecialFilterDef);
                        if (special == null || !special.configurable) return false;
                        into.Specials.Add(special); break;
                    default: return false;
                }
            }
            return true;
        }

        /// <summary>Applies the resolved body to a ThingFilter (live or
        /// scratch). The priority is the caller's to apply, since a scratch
        /// filter has no owner.</summary>
        internal static void Apply(ThingFilter filter, Resolved resolved, ThingFilter? parent, List<ThingDef> universe)
        {
            if (resolved.Preset != null)
                StockpileFilter.Apply(filter, resolved.Preset, new StockpileFilter.Resolved(), new StockpileFilter.Resolved(), parent, universe, null);
            if (resolved.Replace != null)
            {
                filter.SetDisallowAll();
                StockpileFilter.Apply(filter, null, resolved.Replace, new StockpileFilter.Resolved(), parent, universe, null);
            }
            StockpileFilter.Apply(filter, null, resolved.Allow, resolved.Disallow, parent, universe, null);
            if (resolved.HitPoints.HasValue) filter.AllowedHitPointsPercents = resolved.HitPoints.Value;
            if (resolved.Quality.HasValue) filter.AllowedQualityLevels = resolved.Quality.Value;
        }

        /// <summary>The filter the live one would become: a scratch copy of the
        /// live allowances with the body applied on top. Absent parts of the
        /// body preserve whatever the live filter has.</summary>
        internal static ThingFilter Projected(IStoreSettingsParent stockpile, Resolved resolved)
        {
            var scratch = new ThingFilter();
            scratch.CopyAllowancesFrom(stockpile.GetStoreSettings().filter);
            Apply(scratch, resolved, StockpileFilter.ParentFilter(stockpile), StockpileFilter.StorableDefs(stockpile));
            return scratch;
        }

        /// <summary>Exact equality of the parts a stockpile filter round-trips:
        /// allowed def set, configurable special flags and both ranges.</summary>
        internal static bool SameFilter(ThingFilter a, ThingFilter b)
        {
            if (!StockpileFilter.AllowedSet(a).SetEquals(StockpileFilter.AllowedSet(b))) return false;
            if (StockpileFilter.SpecialSignature(a) != StockpileFilter.SpecialSignature(b)) return false;
            return a.AllowedHitPointsPercents.min == b.AllowedHitPointsPercents.min && a.AllowedHitPointsPercents.max == b.AllowedHitPointsPercents.max
                && a.AllowedQualityLevels.min == b.AllowedQualityLevels.min && a.AllowedQualityLevels.max == b.AllowedQualityLevels.max;
        }

        /// <summary>True when the live stockpile equals the projection of the
        /// desired body over its current state.</summary>
        internal static bool Matches(IStoreSettingsParent stockpile, Resolved resolved)
        {
            var settings = stockpile.GetStoreSettings();
            if (resolved.Priority.HasValue && settings.Priority != resolved.Priority.Value) return false;
            return SameFilter(settings.filter, Projected(stockpile, resolved));
        }

        /// <summary>Per-zone CAS token contribution: everything SameFilter
        /// compares, so any filter edit changes the token.</summary>
        internal static void WriteSignature(BinaryWriter w, ThingFilter filter)
        {
            var allowed = StockpileFilter.AllowedSet(filter).Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            w.Write(allowed.Count);
            foreach (var name in allowed) w.Write(name);
            w.Write(StockpileFilter.SpecialSignature(filter));
            w.Write(filter.AllowedHitPointsPercents.min); w.Write(filter.AllowedHitPointsPercents.max);
            w.Write((int)filter.AllowedQualityLevels.min); w.Write((int)filter.AllowedQualityLevels.max);
        }

        // The filter's allowed set, ranges and fresh/rotten flags; AllowRotten has no SpecialThingFilterDefOf member.
        internal static Obs.StockpileFilter Project(ThingFilter filter) => new Obs.StockpileFilter
        {
            AllowedDefNames = [.. StockpileFilter.AllowedSet(filter).Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal)],
            HitPointsMin = filter.AllowedHitPointsPercents.min,
            HitPointsMax = filter.AllowedHitPointsPercents.max,
            QualityMin = ToQuality(filter.AllowedQualityLevels.min),
            QualityMax = ToQuality(filter.AllowedQualityLevels.max),
            AllowFresh = filter.Allows(SpecialThingFilterDefOf.AllowFresh),
            AllowRotten = filter.Allows(DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowRotten") ?? throw new InvalidOperationException("SpecialThingFilterDef AllowRotten is missing.")),
        };    }
}
