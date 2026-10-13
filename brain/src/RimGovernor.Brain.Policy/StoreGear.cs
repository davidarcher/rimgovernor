namespace RimGovernor.Brain.Policy;

// The armory and wardrobe stockpiles: once layout's armory stands, one zone over its free cells keeps weapons
// and armor there; the same for clothing in the wardrobe. They replace the fixed 2x2 weapons and apparel zones:
// until the rooms stand, gear stays in the general store (the stores are the Military department's,
// StoreMilitary.cs). The armor-versus-clothing split is the catalog's (ItemFacts.Armor), and the gear quality
// and hit-point floors ride the filters (StockpileFilter.ArmoryFilter, StockpileFilter.WardrobeFilter).

/// <summary>
/// The catalog names no armor def: without the armor split the armory and wardrobe have no filters, so gear
/// storage cannot be planned. (Go <c>ErrNoArmorDefs</c>.)
/// </summary>
public sealed class NoArmorDefsException()
    : DomainException(
        "the catalog lists no armor defs (ApparelProperties.defaultOutfitTags Soldier without Worker), so the armory and wardrobe cannot be planned"
    );

/// <summary>
/// The colony's serviceable gear (hit points and quality over the gear floors) and the gear stores' filters.
/// Null on a StoreView while the gear census is unread: neither gear room is then asked for or stocked.
/// </summary>
public sealed record GearStore
{
    public required StockpileFilter Armory { get; init; }

    public required StockpileFilter Wardrobe { get; init; }

    /// <summary>The serviceable weapons the colony holds, loose or stored.</summary>
    public int Weapons { get; init; }

    /// <summary>The serviceable armor the colony holds, loose or stored.</summary>
    public int ArmorHeld { get; init; }

    /// <summary>The serviceable clothing the colony holds, loose or stored.</summary>
    public int Clothing { get; init; }

    /// <summary>
    /// Counts the serviceable stored apparel of stored by the catalog's armor split, beside the weapons lying
    /// on the map. Throws <see cref="NoArmorDefsException"/> while the catalog names no armor, and a
    /// <see cref="DomainException"/> when the armor defs do not make valid filters.
    /// </summary>
    public static GearStore NewGearStore(ItemFacts items, IReadOnlyList<GearStock> stored, int weapons)
    {
        if (items.Armor.Count == 0)
        {
            throw new NoArmorDefsException();
        }
        var (armory, wardrobe) = PolicyFunctions.GearFilters(items.Armor);
        var armorDefs = new HashSet<Resource>(items.Armor);
        var armorHeld = 0;
        var clothing = 0;
        foreach (var row in stored)
        {
            if (!row.Serviceable())
            {
            }
            else if (armorDefs.Contains(row.Definition))
            {
                armorHeld += row.Count;
            }
            else
            {
                clothing += row.Count;
            }
        }
        return new GearStore { Armory = armory, Wardrobe = wardrobe, Weapons = weapons, ArmorHeld = armorHeld, Clothing = clothing };
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The armory's and the wardrobe's filters for the catalog's armor defs.</summary>
    public static (StockpileFilter Armory, StockpileFilter Wardrobe) GearFilters(IReadOnlyList<Resource> armor)
    {
        var defs = armor.Select(def => def.Value).ToList();
        return (StockpileFilter.ArmoryFilter(defs), StockpileFilter.WardrobeFilter(defs));
    }
}

public sealed partial record StoreView
{
    /// <summary>
    /// Reports a gear room demand asks for that the plan holds but does not yet stand: it will take gear out of
    /// the warehouse, so the warehouse waits before asking for another storage room.
    /// </summary>
    internal bool GearRoomPending(RoomDemand demand)
    {
        if (Layout is null || Rooms is null)
        {
            return false;
        }
        foreach (var planned in Layout.Rooms)
        {
            if ((planned.Role == PlannedRole.Armory && demand.Armory) || (planned.Role == PlannedRole.Wardrobe && demand.Wardrobe))
            {
                // Census: pending until the room is a roofed room a zone can bind to.
                if (!PolicyFunctions.CensusRoomIn(planned, Rooms).Ok)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
