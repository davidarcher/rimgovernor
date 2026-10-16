namespace RimGovernor.Brain.Policy;

// The gear stockpiles: a weapons zone and an apparel zone, fixed patches inside the first warehouse that has room
// for them (the stores are the Military department's, StoreMilitary.cs). The armor-versus-clothing split is the
// catalog's (ItemFacts.Armor), and the gear quality and hit-point floors ride the filters
// (StockpileFilter.ArmoryFilter, StockpileFilter.WardrobeFilter).

/// <summary>
/// The catalog names no armor def: without the armor split the gear stores have no filters, so gear storage
/// cannot be planned. (Go <c>ErrNoArmorDefs</c>.)
/// </summary>
public sealed class NoArmorDefsException()
    : DomainException(
        "the catalog lists no armor defs (ApparelProperties.defaultOutfitTags Soldier without Worker), so the gear stores cannot be planned"
    );

/// <summary>The gear stores' filters: weapons and armor in the weapons zone, clothing in the apparel zone.</summary>
public sealed record GearStore
{
    public required StockpileFilter Weapons { get; init; }

    public required StockpileFilter Apparel { get; init; }

    /// <summary>
    /// Builds the filters from the catalog's armor split. Throws <see cref="NoArmorDefsException"/> while the
    /// catalog names no armor, and a <see cref="DomainException"/> when the armor defs do not make valid filters.
    /// </summary>
    public static GearStore NewGearStore(ItemFacts items)
    {
        if (items.Armor.Count == 0)
        {
            throw new NoArmorDefsException();
        }
        var (weapons, apparel) = PolicyFunctions.GearFilters(items.Armor);
        return new GearStore { Weapons = weapons, Apparel = apparel };
    }
}

/// <summary>Where the gear zones stand: each a fixed patch inside a planned warehouse, null when no warehouse has a place for it.</summary>
public readonly record struct GearPatches(Rectangle? Apparel, Rectangle? Weapons)
{
    /// <summary>The cells both patches take, which the warehouse zone leaves out.</summary>
    internal List<Cell> Cells()
    {
        var cells = new List<Cell>();
        if (Apparel is { } a)
        {
            cells.AddRange(PolicyFunctions.RectCells(a));
        }
        if (Weapons is { } w)
        {
            cells.AddRange(PolicyFunctions.RectCells(w));
        }
        return cells;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The side of a gear zone's square patch.</summary>
    internal const int GearPatchSize = 3;

    /// <summary>
    /// The gear zones' places, from the plan alone: the apparel patch is the first free square of the first
    /// planned warehouse, the weapons patch the first free square in plan order whose every cell lies outside the
    /// weapon clearance of every prison (NearPrison), so weapons are never stored where a prisoner could reach
    /// them. A warehouse with no such square hosts no weapons zone.
    /// </summary>
    public static GearPatches GearPatchesOf(LayoutPlan plan)
    {
        var prisons = PrisonCells(plan);
        var rooms = plan.Rooms.Where(p => p.Role == PlannedRole.Storage).ToList();
        Rectangle? apparel = null;
        Rectangle? weapons = null;
        foreach (var room in rooms)
        {
            var floor = CellSet(room.Region.Cells());
            apparel ??= FirstSquare(floor, [], []);
            weapons ??= FirstSquare(floor, apparel is { } a ? RectCells(a) : [], prisons);
        }
        return new GearPatches(apparel, weapons);
    }

    /// <summary>The first GearPatchSize square, in row order, whose cells all lie in floor, none in taken and none near a prison.</summary>
    private static Rectangle? FirstSquare(HashSet<Cell> floor, IReadOnlyList<Cell> taken, IReadOnlyList<Cell> prisons)
    {
        var skip = CellSet(taken);
        foreach (var origin in floor.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            var square = new Rectangle(origin.X, origin.Z, GearPatchSize, GearPatchSize);
            var cells = RectCells(square);
            if (cells.All(floor.Contains) && !cells.Any(skip.Contains) && (prisons.Count == 0 || !NearPrison(cells, prisons)))
            {
                return square;
            }
        }
        return null;
    }

    /// <summary>The weapons zone's and the apparel zone's filters for the catalog's armor defs.</summary>
    public static (StockpileFilter Weapons, StockpileFilter Apparel) GearFilters(IReadOnlyList<Resource> armor)
    {
        var defs = armor.Select(def => def.Value).ToList();
        return (StockpileFilter.ArmoryFilter(defs), StockpileFilter.WardrobeFilter(defs));
    }
}
