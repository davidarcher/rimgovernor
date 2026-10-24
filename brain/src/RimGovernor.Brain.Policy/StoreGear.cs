namespace RimGovernor.Brain.Policy;

// The gear stockpiles: a weapons zone and an apparel zone, fixed patches inside the first warehouse that has room
// for them (the stores are the Military department's, StoreMilitary.cs). The armor-versus-clothing split is the
// catalog's (ItemFacts.Armor), and the gear quality and hit-point floors ride the filters
// (StockpileFilter.ArmoryFilter, StockpileFilter.WardrobeFilter).

/// <summary>
/// The catalog names no armor def: without the armor split the gear stores have no filters, so gear storage
/// cannot be planned.
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
        var (weapons, apparel) = StoreRules.GearFilters(items.Armor);
        return new GearStore { Weapons = weapons, Apparel = apparel };
    }
}

/// <summary>Where one gear zone stands (<see cref="GearPatches"/>).</summary>
public abstract record GearPatch
{
    private GearPatch()
    {
    }

    /// <summary>A fixed square inside a planned warehouse.</summary>
    public sealed record Placed(Rectangle Square) : GearPatch;

    /// <summary>No planned warehouse has a place for the zone.</summary>
    public sealed record NoRoom : GearPatch;
}

/// <summary>Where the gear zones stand: each a fixed patch inside a planned warehouse, or no room for it.</summary>
public readonly record struct GearPatches(GearPatch Apparel, GearPatch Weapons)
{
    /// <summary>The cells both patches take, which the warehouse zone leaves out.</summary>
    internal List<Cell> Cells()
    {
        var cells = new List<Cell>();
        if (Apparel is GearPatch.Placed a)
        {
            cells.AddRange(PlannedRules.RectCells(a.Square));
        }
        if (Weapons is GearPatch.Placed w)
        {
            cells.AddRange(PlannedRules.RectCells(w.Square));
        }
        return cells;
    }
}

public static partial class StoreRules
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
        var prisons = PrisonLayoutRules.PrisonCells(plan);
        var rooms = plan.Rooms.Where(p => p.Role == PlannedRole.Storage).ToList();
        GearPatch apparel = new GearPatch.NoRoom();
        GearPatch weapons = new GearPatch.NoRoom();
        foreach (var room in rooms)
        {
            var floor = StockpileRules.CellSet(room.Region.Cells());
            if (apparel is GearPatch.NoRoom)
            {
                apparel = FirstSquare(floor, [], []);
            }
            if (weapons is GearPatch.NoRoom)
            {
                weapons = FirstSquare(floor, apparel is GearPatch.Placed a ? PlannedRules.RectCells(a.Square) : [], prisons);
            }
        }
        return new GearPatches(apparel, weapons);
    }

    /// <summary>The first GearPatchSize square, in row order, whose cells all lie in floor, none in taken and none near a prison; no room when there is none.</summary>
    private static GearPatch FirstSquare(HashSet<Cell> floor, IReadOnlyList<Cell> taken, IReadOnlyList<Cell> prisons)
    {
        var skip = StockpileRules.CellSet(taken);
        foreach (var origin in floor.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            var square = new Rectangle(origin.X, origin.Z, GearPatchSize, GearPatchSize);
            var cells = PlannedRules.RectCells(square);
            if (cells.All(floor.Contains) && !cells.Any(skip.Contains) && (prisons.Count == 0 || !PrisonLayoutRules.NearPrison(cells, prisons)))
            {
                return new GearPatch.Placed(square);
            }
        }
        return new GearPatch.NoRoom();
    }

    /// <summary>The weapons zone's and the apparel zone's filters for the catalog's armor defs.</summary>
    public static (StockpileFilter Weapons, StockpileFilter Apparel) GearFilters(IReadOnlyList<Resource> armor)
    {
        var defs = armor.Select(def => def.Value).ToList();
        return (StockpileFilter.ArmoryFilter(defs), StockpileFilter.WardrobeFilter(defs));
    }
}
