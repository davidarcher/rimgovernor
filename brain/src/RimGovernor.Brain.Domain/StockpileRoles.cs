namespace RimGovernor.Brain.Domain;

public static partial class DomainConstants
{
    // Stockpile role keys. A planner claims the zones it creates by role; parameterized roles append ":<key>".

    /// <summary>The warehouse: the roofed general store.</summary>
    public const string GeneralRole = "general";

    /// <summary>The starter shelter's floor: a general store at Low priority until a real storage room stands.</summary>
    public const string ShelterRole = "shelter";
    public const string IngredientsPrefix = "ingredients:";
    public const string MealsRolePrefix = "meals:";
    public const string RawFoodRolePrefix = "rawfood:";
    public const string RawMeatRolePrefix = "rawmeat:";
    public const string RawVegRolePrefix = "rawveg:";
    public const string CorpsesRolePrefix = "corpses:";
    public const string PerishablesRolePrefix = "perishables:";
    public const string TombRolePrefix = "tomb:";
    public const string MorgueRolePrefix = "morgue:";

    /// <summary>The materials yard: unroofed, Low priority.</summary>
    public const string YardRole = "yard";
    public const string MedicineRolePrefix = "medicine:";

    /// <summary>The armory: weapons and armor, filling its room.</summary>
    public const string ArmoryRolePrefix = "armory:";

    /// <summary>The wardrobe: clothing, filling its room.</summary>
    public const string WardrobeRolePrefix = "wardrobe:";

    /// <summary>The waste yard's one dump zone, a Sanitation store.</summary>
    public const string DumpRole = "wastedump";

    /// <summary>The walled incinerator's zone, a Sanitation store.</summary>
    public const string IncineratorRole = "incinerator";

    /// <summary>
    /// Gear stockpiles keep serviceable gear only: at least half its hit points and Normal quality. What falls
    /// below is burnable waste or sale gear.
    /// </summary>
    public const double GearHitPointFloor = 0.5;

    public const Quality GearQualityFloor = Quality.Normal;
}

public sealed partial record StockpileFilter
{
    private static StockpileFilter GearFilter(IEnumerable<FilterSelector> allow, IEnumerable<FilterSelector> disallow) =>
        new StockpileFilter(FilterBase.Nothing, allow, disallow)
            .WithHitPoints(DomainConstants.GearHitPointFloor, 1)
            .WithQuality(DomainConstants.GearQualityFloor, Quality.Legendary);

    /// <summary>Go <c>IsArmoryFilter</c>: recognises the armory's filter by its shape (a gear floor over weapons).</summary>
    public static bool IsArmoryFilter(StockpileFilter f) =>
        f.HitPoints is not null
        && f.QualityRange is not null
        && f.Base == FilterBase.Nothing
        && HasSelector(f.Allow, FilterSelector.CategoryDef("Weapons"))
        && !HasSelector(f.Allow, FilterSelector.CategoryDef("Apparel"));

    /// <summary>Go <c>IsWardrobeFilter</c>: recognises the wardrobe's filter by its shape (a gear floor over apparel without weapons).</summary>
    public static bool IsWardrobeFilter(StockpileFilter f) =>
        f.HitPoints is not null
        && f.QualityRange is not null
        && f.Base == FilterBase.Nothing
        && HasSelector(f.Allow, FilterSelector.CategoryDef("Apparel"))
        && !HasSelector(f.Allow, FilterSelector.CategoryDef("Weapons"));

    private static bool HasSelector(EquatableArray<FilterSelector> rows, FilterSelector s)
    {
        foreach (var r in rows)
        {
            if (r == s)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Usable weapons and armor: no biocoded weapons or apparel, no tainted (dead man's) apparel, nothing burnable
    /// (the sell and burn boundary), hit points and quality above the gear floors. <paramref name="armor"/> names
    /// the apparel defs that count as armor (the catalog's ItemFacts.Armor); every other apparel def is the
    /// wardrobe's.
    /// </summary>
    public static StockpileFilter ArmoryFilter(IEnumerable<string> armor)
    {
        var allow = new List<FilterSelector> { FilterSelector.CategoryDef("Weapons") };
        allow.AddRange(armor.Select(FilterSelector.ThingDef));
        return GearFilter(
            allow,
            [
                FilterSelector.SpecialFilter("AllowBiocodedWeapons"),
                FilterSelector.SpecialFilter("AllowBiocodedApparel"),
                FilterSelector.SpecialFilter("AllowDeadmansApparel"),
                FilterSelector.SpecialFilter(DomainConstants.BurnableFilterDef),
            ]
        );
    }

    /// <summary>
    /// Wearable clothing: all apparel but the armor defs, no tainted or biocoded pieces, hit points and quality
    /// above the gear floors.
    /// </summary>
    public static StockpileFilter WardrobeFilter(IEnumerable<string> armor)
    {
        var disallow = new List<FilterSelector>
        {
            FilterSelector.SpecialFilter("AllowDeadmansApparel"),
            FilterSelector.SpecialFilter("AllowBiocodedApparel"),
            FilterSelector.SpecialFilter(DomainConstants.BurnableFilterDef),
        };
        disallow.AddRange(armor.Select(FilterSelector.ThingDef));
        return GearFilter([FilterSelector.CategoryDef("Apparel")], disallow);
    }

    /// <summary>
    /// The waste yard dump's filter: all storable items except what the native rule calls not burnable, so the
    /// burnable waste lies there for the incinerator and the rest stays in the warehouse. It is the incinerator's
    /// filter at Low priority.
    /// </summary>
    public static StockpileFilter DumpFilter() => IncineratorFilter();

    /// <summary>
    /// What the incinerator takes: everything the mod's native burnable rule does not refuse
    /// (<see cref="DomainConstants.NotBurnableFilterDef"/>). Burnable is defined once, natively: rottables past
    /// Fresh except colonist and slave corpses, and gear below the static silver cutoff. The zone outranks the Low
    /// dump, so waste hauls in from it.
    /// </summary>
    public static StockpileFilter IncineratorFilter() => new(FilterBase.Everything, null, [FilterSelector.SpecialFilter(DomainConstants.NotBurnableFilterDef)]);
}
