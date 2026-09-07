// Extracted from ZoneCellsTool.cs; see ../PROVENANCE.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class StockpileFilter
    {
        internal const int SampleSize = 10;

        internal static readonly string[] PresetNames =
            { "everything", "nothing", "food", "perishables", "nonperishables", "outdoorSafe" };

        // ------------------------------------------------------------ universe

        /// <summary>
        /// The stockpile's parent filter -- what it could ever be told to hold.
        /// Zone_Stockpile.GetParentStoreSettings() returns the shared
        /// EverStorableFixedSettings singleton; null when the read throws.
        /// </summary>
        internal static ThingFilter ParentFilter(Zone_Stockpile stockpile)
        {
            try
            {
                var parent = stockpile == null
                    ? StorageSettings.EverStorableFixedSettings()
                    : stockpile.GetParentStoreSettings();
                return parent == null ? null : parent.filter;
            }
            catch { return null; }
        }

        /// <summary>
        /// Every def the universe holds, in DefDatabase order so a sample is
        /// stable between calls. Falls back to the DefDatabase sweep the parent
        /// filter itself performs if the parent cannot be read.
        /// </summary>
        internal static List<ThingDef> StorableDefs(Zone_Stockpile stockpile)
        {
            var universe = new List<ThingDef>();
            var parent = ParentFilter(stockpile);
            var allowed = new HashSet<ThingDef>();
            if (parent != null)
            {
                try
                {
                    foreach (var def in parent.AllowedThingDefs)
                        if (def != null)
                            allowed.Add(def);
                }
                catch { allowed.Clear(); }
            }

            try
            {
                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (def == null)
                        continue;
                    var local = def;
                    var storable = allowed.Count > 0
                        ? allowed.Contains(local)
                        : BridgeCommon.Try(() => local.EverStorable(true), false);
                    if (storable)
                        universe.Add(local);
                }
            }
            catch { }

            return universe;
        }

        // ------------------------------------------------------------- presets

        /// <summary>The canonical preset name, or null when the text names none.</summary>
        internal static string NormalizePreset(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var trimmed = text.Trim();
            foreach (var name in PresetNames)
                if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                    return name;
            return null;
        }

        /// <summary>Carries a rot timer. HasComp&lt;T&gt; matches subclasses of
        /// CompRottable too; HasComp(Type) compares compClass by reference and
        /// would miss them.</summary>
        internal static bool IsRottable(ThingDef def)
        {
            return BridgeCommon.Try(() => def.HasComp<CompRottable>(), false);
        }

        /// <summary>ThingDef.IsNutritionGivingIngestible: an ingestible whose
        /// cached nutrition is above zero.</summary>
        internal static bool IsFood(ThingDef def)
        {
            return BridgeCommon.Try(() => def.IsNutritionGivingIngestible, false);
        }

        /// <summary>Neither rots nor weathers. CanEverDeteriorate is checked
        /// alongside the rate because a def with useHitPoints false never
        /// deteriorates whatever its stat says.</summary>
        internal static bool IsOutdoorSafe(ThingDef def)
        {
            if (IsRottable(def))
                return false;
            if (!BridgeCommon.Try(() => def.CanEverDeteriorate, true))
                return true;
            return BridgeCommon.Try(() => def.GetStatValueAbstract(StatDefOf.DeteriorationRate, null), 1f) <= 0f;
        }

        /// <summary>The defs a computed preset allows. Null for the two presets
        /// that are the game's own buttons rather than a def set.</summary>
        internal static List<ThingDef> PresetDefs(string preset, List<ThingDef> universe)
        {
            if (preset == "food")
                return universe.Where(IsFood).ToList();
            if (preset == "perishables")
                return universe.Where(IsRottable).ToList();
            if (preset == "nonperishables")
                return universe.Where(d => !IsRottable(d)).ToList();
            if (preset == "outdoorSafe")
                return universe.Where(IsOutdoorSafe).ToList();
            return null;
        }

        /// <summary>One sentence per preset the call used, so the reply defines
        /// what it did rather than leaving a caller to guess.</summary>
        internal static Dictionary<string, object> Definition(string preset)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (preset == null)
                return d;
            switch (preset)
            {
                case "everything":
                    d["everything"] = "Every def a stockpile can ever hold: ThingFilter.SetAllowAll(parent filter), which is the storage tab's own Allow All button. The parent filter is StorageSettings.EverStorableFixedSettings() -- the Root thing category restricted to ThingDef.EverStorable(true), so minifiable furniture is in.";
                    break;
                case "nothing":
                    d["nothing"] = "Nothing at all: ThingFilter.SetDisallowAll(), which is the storage tab's own Clear All button. It also re-enables every configurable special filter, exactly as that button does.";
                    break;
                case "food":
                    d["food"] = "Storable defs where ThingDef.IsNutritionGivingIngestible is true -- an ingestible whose nutrition is above zero. That is the game's own 'has food value' test, so kibble, hay and other animal feed ARE included, as are nutrition-carrying drugs; it is not the human-meals subset.";
                    break;
                case "perishables":
                    d["perishables"] = "Storable defs carrying a CompRottable (ThingDef.HasComp<CompRottable>(), which matches subclasses): raw food, meals, corpses, anything with a rot timer.";
                    break;
                case "nonperishables":
                    d["nonperishables"] = "Every storable def that is NOT perishable -- the exact complement of the perishables set over the same universe, so the two counts sum to storableDefCount.";
                    break;
                case "outdoorSafe":
                    d["outdoorSafe"] = "Storable defs that neither rot nor weather: no CompRottable, and either ThingDef.CanEverDeteriorate is false or GetStatValueAbstract(StatDefOf.DeteriorationRate) is at most zero -- the game's own deterioration number. A subset of nonperishables.";
                    break;
            }
            return d;
        }

        // ------------------------------------------------------- name resolving

        /// <summary>What an allow/disallow list parsed into. Unresolved names are
        /// the reason a call is refused before anything is written.</summary>
        internal sealed class Resolved
        {
            internal readonly List<ThingCategoryDef> Categories = new List<ThingCategoryDef>();
            internal readonly List<ThingDef> Defs = new List<ThingDef>();
            internal readonly List<string> Unresolved = new List<string>();
            internal bool Any { get { return Categories.Count > 0 || Defs.Count > 0; } }
        }

        /// <summary>
        /// Split on commas, semicolons and newlines, then resolve each name to a
        /// ThingCategoryDef or a ThingDef. Search order is fixed so the same text
        /// always resolves to the same def: category defName, thing defName,
        /// category label, thing label.
        /// </summary>
        internal static Resolved Resolve(string spec)
        {
            var result = new Resolved();
            if (string.IsNullOrEmpty(spec))
                return result;

            foreach (var raw in spec.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = raw.Trim();
                if (name.Length == 0)
                    continue;

                var category = FindCategory(name, true);
                if (category != null) { result.Categories.Add(category); continue; }

                var def = FindThing(name, true);
                if (def != null) { result.Defs.Add(def); continue; }

                category = FindCategory(name, false);
                if (category != null) { result.Categories.Add(category); continue; }

                def = FindThing(name, false);
                if (def != null) { result.Defs.Add(def); continue; }

                result.Unresolved.Add(name);
            }
            return result;
        }

        private static ThingCategoryDef FindCategory(string name, bool byDefName)
        {
            try
            {
                foreach (var c in DefDatabase<ThingCategoryDef>.AllDefs)
                {
                    if (c == null)
                        continue;
                    var text = byDefName ? c.defName : c.label;
                    if (!string.IsNullOrEmpty(text) && string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
            }
            catch { }
            return null;
        }

        private static ThingDef FindThing(string name, bool byDefName)
        {
            try
            {
                foreach (var d in DefDatabase<ThingDef>.AllDefs)
                {
                    if (d == null)
                        continue;
                    var text = byDefName ? d.defName : d.label;
                    if (!string.IsNullOrEmpty(text) && string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Whether the thing-category tree is up. SetAllow on a
        /// ThingCategoryDef calls Log.Error when it is not, and Log.Error pauses
        /// the game, so a call naming a category is refused instead.</summary>
        internal static bool CategoryTreeReady()
        {
            return BridgeCommon.Try(() => ThingCategoryNodeDatabase.initialized, false);
        }

        // --------------------------------------------------------------- apply

        /// <summary>
        /// Preset first, then allow, then disallow -- the order a caller reads
        /// the arguments in. `changes` collects one English sentence per step and
        /// may be null. The filter is either the live one or a scratch copy; this
        /// method cannot tell and does not need to.
        /// </summary>
        internal static void Apply(ThingFilter filter, string preset, Resolved allow, Resolved disallow,
                                   ThingFilter parent, List<ThingDef> universe, List<object> changes)
        {
            if (preset == "everything")
            {
                filter.SetAllowAll(parent);
                if (changes != null)
                    changes.Add("Allow everything a stockpile can hold (" + universe.Count + " defs) -- the storage tab's Allow All.");
            }
            else if (preset == "nothing")
            {
                filter.SetDisallowAll();
                if (changes != null)
                    changes.Add("Disallow everything -- the storage tab's Clear All.");
            }
            else if (preset != null)
            {
                var defs = PresetDefs(preset, universe);
                filter.SetDisallowAll();
                foreach (var def in defs)
                    filter.SetAllow(def, true);
                if (changes != null)
                    changes.Add("Set the filter from preset \"" + preset + "\": " + defs.Count
                                + " of " + universe.Count + " storable defs allowed.");
            }

            foreach (var category in allow.Categories)
            {
                filter.SetAllow(category, true);
                if (changes != null)
                    changes.Add("Allow category " + Label(category) + " and everything under it.");
            }
            foreach (var def in allow.Defs)
            {
                filter.SetAllow(def, true);
                if (changes != null)
                    changes.Add("Allow " + Label(def) + ".");
            }
            foreach (var category in disallow.Categories)
            {
                filter.SetAllow(category, false);
                if (changes != null)
                    changes.Add("Disallow category " + Label(category) + " and everything under it.");
            }
            foreach (var def in disallow.Defs)
            {
                filter.SetAllow(def, false);
                if (changes != null)
                    changes.Add("Disallow " + Label(def) + ".");
            }
        }

        /// <summary>The allowed defs as a set, for a before/after comparison that
        /// does not depend on enumeration order.</summary>
        internal static HashSet<ThingDef> AllowedSet(ThingFilter filter)
        {
            var set = new HashSet<ThingDef>();
            if (filter == null)
                return set;
            try
            {
                foreach (var def in filter.AllowedThingDefs)
                    if (def != null)
                        set.Add(def);
            }
            catch { }
            return set;
        }

        // ------------------------------------------------------------- summary

        /// <summary>
        /// The block home/list_zones and home/zone_cells both emit for a
        /// stockpile filter. Every count is over the same universe, so
        /// allowedDefCount and storableDefCount are directly comparable and
        /// perishables + nonperishables sum to storableDefCount exactly.
        /// </summary>
        internal static Dictionary<string, object> Summary(ThingFilter filter, StoragePriority? priority,
                                                           List<ThingDef> universe)
        {
            var summary = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "allowedDefCount", null },
                { "storableDefCount", universe == null ? 0 : universe.Count },
                { "priority", priority.HasValue ? priority.Value.ToString() : null },
                { "categoriesFullyAllowed", new List<object>() },
                { "categoriesPartlyAllowed", new List<object>() },
                { "allowsRottable", false },
                { "allowedRottableCount", 0 },
                { "sampleAllowed", new List<object>() },
                { "sampleTruncated", false }
            };
            if (filter == null || universe == null)
                return summary;

            var allowed = AllowedSet(filter);
            var inUniverse = universe.Where(allowed.Contains).ToList();
            summary["allowedDefCount"] = BridgeCommon.Try(() => filter.AllowedDefCount, inUniverse.Count);

            var rottable = inUniverse.Count(IsRottable);
            summary["allowedRottableCount"] = rottable;
            summary["allowsRottable"] = rottable > 0;

            var sample = new List<object>();
            foreach (var def in inUniverse)
            {
                if (sample.Count >= SampleSize)
                    break;
                sample.Add(Label(def));
            }
            summary["sampleAllowed"] = sample;
            summary["sampleTruncated"] = inUniverse.Count > sample.Count;

            var full = new List<object>();
            var part = new List<object>();
            foreach (var category in TopLevelCategories())
            {
                var storableChildren = StorableDescendants(category, universe);
                if (storableChildren.Count == 0)
                    continue;
                var allowedChildren = storableChildren.Count(allowed.Contains);
                if (allowedChildren == storableChildren.Count)
                    full.Add(Label(category));
                else if (allowedChildren > 0)
                    part.Add(Label(category));
            }
            summary["categoriesFullyAllowed"] = full;
            summary["categoriesPartlyAllowed"] = part;
            return summary;
        }

        /// <summary>The categories the storage tab shows at the top of its tree:
        /// the children of ThingCategoryDefOf.Root.</summary>
        private static List<ThingCategoryDef> TopLevelCategories()
        {
            try
            {
                var root = ThingCategoryDefOf.Root;
                if (root == null || root.childCategories == null)
                    return new List<ThingCategoryDef>();
                return root.childCategories.Where(c => c != null).ToList();
            }
            catch { return new List<ThingCategoryDef>(); }
        }

        private static List<ThingDef> StorableDescendants(ThingCategoryDef category, List<ThingDef> universe)
        {
            try
            {
                var inCategory = new HashSet<ThingDef>(category.DescendantThingDefs.Where(d => d != null));
                return universe.Where(inCategory.Contains).ToList();
            }
            catch { return new List<ThingDef>(); }
        }

        internal static string Label(Def def)
        {
            if (def == null)
                return "(null)";
            var label = BridgeCommon.SafeString(() => def.label);
            if (!string.IsNullOrEmpty(label))
                return label;
            return BridgeCommon.SafeString(() => def.defName) ?? "(unnamed)";
        }
    }
}
