using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// The native storage filter preset a <see cref="StockpileFilter"/> starts from (native FilterPreset) before its
/// allow/disallow selectors apply. Go <c>FilterBase</c>.
/// </summary>
public enum FilterBase
{
    Everything,
    Nothing,
    Food,
    Perishables,
    Nonperishables,
    OutdoorSafe,
    IndoorOnly,
}

/// <summary>
/// Names what a <see cref="FilterSelector"/>'s name resolves against natively. The declaration order is the
/// canonical sort order of a selector list. Go <c>SelectorKind</c>.
/// </summary>
public enum SelectorKind
{
    ThingDef,
    CategoryDef,

    [WireName("special_filter_def")]
    SpecialFilter,
}

/// <summary>One native FilterSelector: exactly one def name of one kind. Go <c>FilterSelector</c>.</summary>
public readonly record struct FilterSelector(SelectorKind Kind, string Name)
{
    /// <summary>Go <c>ThingDef(name)</c>.</summary>
    public static FilterSelector ThingDef(string name) => new(SelectorKind.ThingDef, name);

    /// <summary>Go <c>CategoryDef(name)</c>.</summary>
    public static FilterSelector CategoryDef(string name) => new(SelectorKind.CategoryDef, name);

    /// <summary>Go <c>SpecialFilter(name)</c>.</summary>
    public static FilterSelector SpecialFilter(string name) => new(SelectorKind.SpecialFilter, name);
}

/// <summary>A native QualityCategory name, in native order (Awful 0 .. Legendary 6). Go <c>Quality</c>.</summary>
public enum Quality
{
    [WireName("Awful")]
    Awful,

    [WireName("Poor")]
    Poor,

    [WireName("Normal")]
    Normal,

    [WireName("Good")]
    Good,

    [WireName("Excellent")]
    Excellent,

    [WireName("Masterwork")]
    Masterwork,

    [WireName("Legendary")]
    Legendary,
}

public static class QualityExtensions
{
    /// <summary>Go <c>QualityRank</c>: the index in native QualityCategory order, -1 for a value outside the set.</summary>
    public static int Rank(this Quality q) => Wire.IsDefined(q) ? (int)q : -1;
}

/// <summary>A hit-point fraction range in [0,1].</summary>
public readonly record struct HitPointRange(double Min, double Max);

/// <summary>A quality range (Awful..Legendary).</summary>
public readonly record struct QualityRange(Quality Min, Quality Max);

/// <summary>
/// The closed, comparable value of one storage filter (a stockpile zone's or a storage building's): a native base
/// preset, then canonical (sorted, deduplicated, at most 32 each, disjoint) allow and disallow selectors, then
/// optional hit-point fraction and quality ranges. It maps 1:1 onto native StockpileSettings.preset + FilterPatch.
/// Go <c>StockpileFilter</c> (<c>NewStockpileFilter</c> is the constructor).
/// </summary>
public sealed partial record StockpileFilter
{
    /// <summary>
    /// Canonicalizes a base preset plus allow/disallow selectors. A selector may not be both allowed and
    /// disallowed.
    /// </summary>
    public StockpileFilter(FilterBase @base, IEnumerable<FilterSelector>? allow = null, IEnumerable<FilterSelector>? disallow = null)
    {
        DomainException.Require(Wire.IsDefined(@base), "invalid filter base");
        var a = CanonicalSelectors(allow ?? []);
        var d = CanonicalSelectors(disallow ?? []);
        var seen = new HashSet<FilterSelector>(a);
        foreach (var s in d)
        {
            DomainException.Require(!seen.Contains(s), "selector both allowed and disallowed");
        }
        Base = @base;
        Allow = a;
        Disallow = d;
    }

    public FilterBase Base { get; }

    public EquatableArray<FilterSelector> Allow { get; }

    public EquatableArray<FilterSelector> Disallow { get; }

    /// <summary>Go <c>HitPoints() (min, max, ok)</c>: null when the filter has no hit-point bound.</summary>
    public HitPointRange? HitPoints { get; private init; }

    /// <summary>Go <c>Quality() (min, max, ok)</c>: null when the filter has no quality bound.</summary>
    public QualityRange? QualityRange { get; private init; }

    private static EquatableArray<FilterSelector> CanonicalSelectors(IEnumerable<FilterSelector> rows)
    {
        var sorted = rows
            .OrderBy(s => Wire.IsDefined(s.Kind) ? (int)s.Kind : -1)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i];
            DomainException.Require(Wire.IsDefined(s.Kind) && Identity.IsValidText(s.Name) && !(i > 0 && sorted[i - 1] == s), "invalid filter selector");
        }
        return EquatableArray.From(sorted);
    }

    /// <summary>Bounds the filter to a hit-point fraction range in [0,1].</summary>
    public StockpileFilter WithHitPoints(double min, double max)
    {
        DomainException.Require(!double.IsNaN(min) && !double.IsNaN(max) && min >= 0 && max <= 1 && min <= max, "invalid hit-point range");
        return this with { HitPoints = new HitPointRange(min, max) };
    }

    /// <summary>Bounds the filter to a quality range (Awful..Legendary).</summary>
    public StockpileFilter WithQuality(Quality min, Quality max)
    {
        var lo = min.Rank();
        var hi = max.Rank();
        DomainException.Require(lo >= 0 && hi >= 0 && lo <= hi, "invalid quality range");
        return this with { QualityRange = new QualityRange(min, max) };
    }

    /// <summary>The planners' named filter FoodFilter: the native food preset, unpatched.</summary>
    public static StockpileFilter FoodFilter() => new(FilterBase.Food);

    /// <summary>
    /// The warehouse's: every storable that is not safe outside, plus packed buildings, less what burns: the waste
    /// yard takes the burnable. A building def cannot deteriorate, so the indoor_only preset never lists one; the
    /// Buildings category adds the packed furniture, benches and art clearance leaves behind.
    /// </summary>
    public static StockpileFilter GeneralFilter() =>
        new(FilterBase.IndoorOnly, [FilterSelector.CategoryDef("Buildings")], [FilterSelector.SpecialFilter(DomainConstants.BurnableFilterDef)]);

    /// <summary>The opening outdoor store's: every non-perishable storable except chunks.</summary>
    public static StockpileFilter OpeningStoreFilter() => new(FilterBase.Nonperishables, null, [FilterSelector.CategoryDef("Chunks")]);

    /// <summary>The items safe outside, the native outdoor_safe preset.</summary>
    public static StockpileFilter YardFilter() => new(FilterBase.OutdoorSafe);

    /// <summary>Holds fresh animal and insect corpses only.</summary>
    public static StockpileFilter CorpseLarderFilter() =>
        new(
            FilterBase.Nothing,
            [FilterSelector.CategoryDef("CorpsesAnimal"), FilterSelector.CategoryDef("CorpsesInsect"), FilterSelector.SpecialFilter("AllowFresh")],
            [FilterSelector.SpecialFilter("AllowRotten")]
        );

    /// <summary>Holds raw meat and raw plant food, never rotten: the freezer's cooking-ingredient stock.</summary>
    public static StockpileFilter RawFoodFilter() =>
        new(FilterBase.Nothing, [FilterSelector.CategoryDef("MeatRaw"), FilterSelector.CategoryDef("PlantFoodRaw")], [FilterSelector.SpecialFilter("AllowRotten")]);

    /// <summary>Holds the cooked meals of a cold meal store: the meal closet or the freezer shelf by the dining room.</summary>
    public static StockpileFilter MealShelfFilter() => AllowOnlyFilter(["MealFine", "MealLavish", "MealNutrientPaste", "MealSimple", "MealSurvivalPack"]);

    /// <summary>Holds raw meat, never rotten: the freezer's meat shelf.</summary>
    public static StockpileFilter RawMeatFilter() =>
        new(FilterBase.Nothing, [FilterSelector.CategoryDef("MeatRaw")], [FilterSelector.SpecialFilter("AllowRotten")]);

    /// <summary>Holds raw plant food, never rotten: the freezer's produce shelf.</summary>
    public static StockpileFilter RawVegFilter() =>
        new(FilterBase.Nothing, [FilterSelector.CategoryDef("PlantFoodRaw")], [FilterSelector.SpecialFilter("AllowRotten")]);

    /// <summary>
    /// Holds every food and fresh animal corpse that is not yet rotten: the freezer's catch-all under its dedicated
    /// shelves. Reserve food that never rots (survival meals, pemmican) belongs in general storage.
    /// </summary>
    public static StockpileFilter PerishablesFilter() =>
        new(
            FilterBase.Nothing,
            [FilterSelector.CategoryDef("Foods"), FilterSelector.CategoryDef("CorpsesAnimal"), FilterSelector.CategoryDef("CorpsesInsect")],
            [FilterSelector.SpecialFilter("AllowRotten"), FilterSelector.ThingDef("MealSurvivalPack"), FilterSelector.ThingDef("Pemmican")]
        );

    /// <summary>
    /// Holds human corpses, fresh or not: the tomb room's stock until they are buried, entombed or butchered.
    /// Strangers belong to the morgue, so a tie at Critical never draws them in.
    /// </summary>
    public static StockpileFilter TombCorpsesFilter() =>
        new(FilterBase.Nothing, [FilterSelector.CategoryDef("CorpsesHumanlike")], [FilterSelector.SpecialFilter("AllowCorpsesStranger")]);

    /// <summary>
    /// Holds every human corpse, colonist, slave or stranger, fresh or rotten. The morgue ranks below graves and
    /// sarcophagi (MorguePriority), so a colonist corpse waits here only until one has room.
    /// </summary>
    public static StockpileFilter MorgueCorpsesFilter() => new(FilterBase.Nothing, [FilterSelector.CategoryDef("CorpsesHumanlike")]);

    /// <summary>Holds every medicine and nothing else: the hospital's store.</summary>
    public static StockpileFilter MedicineFilter() => new(FilterBase.Nothing, [FilterSelector.CategoryDef("Medicine")]);

    /// <summary>Disallows everything except the named thing definitions (1..32 of them).</summary>
    public static StockpileFilter AllowOnlyFilter(IEnumerable<string> definitions)
    {
        var rows = definitions.Select(FilterSelector.ThingDef).ToList();
        DomainException.Require(rows.Count != 0, "empty allow-only filter");
        return new StockpileFilter(FilterBase.Nothing, rows);
    }

    /// <summary>
    /// Go <c>AllowOnlyDefinitions() ([]string, bool)</c>: an <see cref="AllowOnlyFilter"/>'s thing definitions;
    /// false for any other filter.
    /// </summary>
    public bool TryGetAllowOnlyDefinitions(out EquatableArray<string> definitions)
    {
        definitions = default;
        if (Base != FilterBase.Nothing || Disallow.Count != 0 || HitPoints is not null || QualityRange is not null)
        {
            return false;
        }
        var names = new List<string>();
        foreach (var s in Allow)
        {
            if (s.Kind != SelectorKind.ThingDef)
            {
                return false;
            }
            names.Add(s.Name);
        }
        if (names.Count == 0)
        {
            return false;
        }
        definitions = EquatableArray.From(names);
        return true;
    }
}
