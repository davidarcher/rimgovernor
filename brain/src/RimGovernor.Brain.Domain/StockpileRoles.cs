namespace RimGovernor.Brain.Domain;

public static class StockpileRolesConstants
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

    /// <summary>The weapons zone: weapons and armor, a patch of the warehouse.</summary>
    public const string WeaponsRole = "weapons";

    /// <summary>The apparel zone: clothing, a patch of the warehouse.</summary>
    public const string ApparelRole = "apparel";

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
            .WithHitPoints(StockpileRolesConstants.GearHitPointFloor, 1)
            .WithQuality(StockpileRolesConstants.GearQualityFloor, Quality.Legendary);

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
                FilterSelector.SpecialFilter(ZoneCreateConstants.BurnableFilterDef),
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
            FilterSelector.SpecialFilter(ZoneCreateConstants.BurnableFilterDef),
        };
        disallow.AddRange(armor.Select(FilterSelector.ThingDef));
        return GearFilter([FilterSelector.CategoryDef("Apparel")], disallow);
    }
}
