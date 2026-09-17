#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The typed Operations.StockpileSettings body shared by CreateZone and
    /// PatchStockpile: validation, resolution of every selector against the
    /// def database, application to a live or scratch ThingFilter, exact
    /// live-versus-desired comparison and the ZoneState.filter projection.
    /// A preset is applied first, then a replacement list (disallow all,
    /// allow the list), then allow, then disallow, then the ranges -- the
    /// order the storage tab's own buttons compose in and the order the
    /// legacy StockpileFilter.Apply uses. Every selector must resolve
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
            internal bool ChangesFilter => Preset != null || Replace != null || Allow.Any || Disallow.Any || HitPoints.HasValue || Quality.HasValue;
        }

        internal static string? PresetName(Operations.FilterPreset preset)
        {
            switch (preset)
            {
                case Operations.FilterPreset.Everything: return "everything";
                case Operations.FilterPreset.Nothing: return "nothing";
                case Operations.FilterPreset.Food: return "food";
                case Operations.FilterPreset.Perishables: return "perishables";
                case Operations.FilterPreset.Nonperishables: return "nonperishables";
                case Operations.FilterPreset.OutdoorSafe: return "outdoorSafe";
                default: return null;
            }
        }

        /// <summary>Structural validation without the def database: shapes,
        /// bounds and range coherence. Resolve does the database lookups.</summary>
        internal static bool Valid(Operations.StockpileSettings? settings)
        {
            if (settings == null) return false;
            if (settings.HasPriority && settings.Priority == Operations.StoragePriority.Unspecified) return false;
            if (settings.HasPreset && PresetName(settings.Preset) == null) return false;
            var filter = settings.Filter;
            if (filter == null) return settings.HasPriority || settings.HasPreset;
            var selectors = filter.Allow.Concat(filter.Disallow).Concat(filter.Replace?.Selectors ?? Enumerable.Empty<Operations.FilterSelector>()).ToList();
            if (selectors.Count > 256 || selectors.Any(s => !ValidSelector(s))) return false;
            if (filter.HasHitPointsMin != filter.HasHitPointsMax || filter.HasQualityMin != filter.HasQualityMax) return false;
            if (filter.HasHitPointsMin && (double.IsNaN(filter.HitPointsMin) || double.IsNaN(filter.HitPointsMax)
                || filter.HitPointsMin < 0 || filter.HitPointsMax > 1 || filter.HitPointsMin > filter.HitPointsMax)) return false;
            if (filter.HasQualityMin && (ParseQuality(filter.QualityMin) == null || ParseQuality(filter.QualityMax) == null
                || ParseQuality(filter.QualityMin)!.Value > ParseQuality(filter.QualityMax)!.Value)) return false;
            return true;
        }

        private static bool ValidSelector(Operations.FilterSelector selector)
        {
            switch (selector.DefinitionCase)
            {
                case Operations.FilterSelector.DefinitionOneofCase.ThingDef: return ProtoBoundary.IsIdentifier(selector.ThingDef);
                case Operations.FilterSelector.DefinitionOneofCase.CategoryDef: return ProtoBoundary.IsIdentifier(selector.CategoryDef);
                case Operations.FilterSelector.DefinitionOneofCase.SpecialFilterDef: return ProtoBoundary.IsIdentifier(selector.SpecialFilterDef);
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
        internal static Resolved? Resolve(Operations.StockpileSettings settings, List<ThingDef> universe)
        {
            if (!Valid(settings)) return null;
            var result = new Resolved();
            if (settings.HasPriority)
            {
                result.Priority = NativeZoneCreation.ToNativePriority(settings.Priority);
                if (!result.Priority.HasValue) return null;
            }
            if (settings.HasPreset) result.Preset = PresetName(settings.Preset);
            var filter = settings.Filter;
            if (filter == null) return result;
            var storable = new HashSet<ThingDef>(universe);
            if (filter.Replace != null)
            {
                result.Replace = new StockpileFilter.Resolved();
                if (!ResolveAll(filter.Replace.Selectors, storable, result.Replace)) return null;
            }
            if (!ResolveAll(filter.Allow, storable, result.Allow) || !ResolveAll(filter.Disallow, storable, result.Disallow)) return null;
            if (filter.HasHitPointsMin) result.HitPoints = new FloatRange((float)filter.HitPointsMin, (float)filter.HitPointsMax);
            if (filter.HasQualityMin) result.Quality = new QualityRange(ParseQuality(filter.QualityMin)!.Value, ParseQuality(filter.QualityMax)!.Value);
            return result;
        }

        private static bool ResolveAll(IEnumerable<Operations.FilterSelector> selectors, HashSet<ThingDef> storable, StockpileFilter.Resolved into)
        {
            foreach (var selector in selectors)
            {
                switch (selector.DefinitionCase)
                {
                    case Operations.FilterSelector.DefinitionOneofCase.ThingDef:
                        var def = DefDatabase<ThingDef>.GetNamedSilentFail(selector.ThingDef);
                        if (def == null || !storable.Contains(def)) return false;
                        into.Defs.Add(def); break;
                    case Operations.FilterSelector.DefinitionOneofCase.CategoryDef:
                        var category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(selector.CategoryDef);
                        if (category == null) return false;
                        into.Categories.Add(category); break;
                    case Operations.FilterSelector.DefinitionOneofCase.SpecialFilterDef:
                        var special = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(selector.SpecialFilterDef);
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
        internal static ThingFilter Projected(Zone_Stockpile stockpile, Resolved resolved)
        {
            var scratch = new ThingFilter();
            scratch.CopyAllowancesFrom(stockpile.settings.filter);
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
        internal static bool Matches(Zone_Stockpile stockpile, Resolved resolved)
        {
            if (resolved.Priority.HasValue && stockpile.settings.Priority != resolved.Priority.Value) return false;
            return SameFilter(stockpile.settings.filter, Projected(stockpile, resolved));
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

        internal static Obs.StockpileFilter Project(ThingFilter filter)
        {
            var row = new Obs.StockpileFilter {
                HitPointsMin = filter.AllowedHitPointsPercents.min, HitPointsMax = filter.AllowedHitPointsPercents.max,
                QualityMin = filter.AllowedQualityLevels.min.ToString(), QualityMax = filter.AllowedQualityLevels.max.ToString() };
            var allowed = StockpileFilter.AllowedSet(filter).Select(d => d.defName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            row.AllowedDefNames.AddRange(allowed);
            foreach (var special in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(d => d.configurable).OrderBy(d => d.defName, StringComparer.Ordinal))
                row.SpecialRules.Add(new Obs.FilterSpecialRule { DefName = special.defName, Allowed = filter.Allows(special) });
            var fresh = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowFresh");
            var rotten = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowRotten");
            if (fresh != null) row.AllowFresh = filter.Allows(fresh);
            if (rotten != null) row.AllowRotten = filter.Allows(rotten);
            row.Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)allowed.Count, Returned = (ulong)allowed.Count };
            return row;
        }
    }
}
