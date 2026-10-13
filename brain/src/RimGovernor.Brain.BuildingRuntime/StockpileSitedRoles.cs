namespace RimGovernor.Brain.BuildingRuntime;

// The room-bound stockpile roles. The meal closet, the table cell and the medicine store are declared by their
// departments from the layout plan (Policy foodOwner, medicalOwner); the one still reading a built fact is the
// table cell (TableMeal). The raw-food role is a 2x2 Critical stockpile of raw meat and raw plant food inside the
// planned freezer: Critical because vanilla ranks Preferred below Important, and only a rank above the starter
// Important food zone hauls raw food into the cold.
//
// All are fixed-size: MaintainStockpiles never resizes them.
internal static partial class BuildingRuntimePackage
{
    /// <summary>The fewest meals a day the colony eats before a warm spot by the table pays: fewer, and the meals wait in the store.</summary>
    internal const double MealSpotMinPerDay = 3.0;

    internal static StockpileFilter AllowOnly(params string[] definitions) => StockpileFilter.AllowOnlyFilter(definitions);

    /// <summary>
    /// The one-cell meal store by the dining table, null while a fact it needs is unknown: a spot in the planned
    /// dining room holding the table, while the colony eats at least <see cref="MealSpotMinPerDay"/> meals a day,
    /// else a retirement of the zone. The cell sits off the chairs, nearest where they are.
    /// </summary>
    internal static Pol.MealStore? TableMeal(Observation.ColonyProjection? projection)
    {
        if (projection is null)
        {
            return null;
        }
        if (!projection.LayoutPlan.TryGetValue(out var plan) || !projection.Facts.Comfort.TryGetValue(out var comfort))
        {
            return null;
        }
        var dining = plan.Rooms.Where(planned => planned.Role == Pol.PlannedRole.Dining).ToList();
        if (dining.Count == 0)
        {
            return null;
        }
        var retired = new Pol.MealStore { Dining = dining[0].Region.Bounds(), Filter = new StockpileFilter(FilterBase.Nothing), Retired = true };
        Pol.PlannedRoom room = default!;
        IReadOnlyList<Cell>? adjacent = null;
        foreach (var planned in dining)
        {
            foreach (var s in comfort.Surfaces)
            {
                if (s.Adjacent.Count > 0 && InRectangle(planned.Region.Bounds(), s.Adjacent[0]))
                {
                    room = planned;
                    adjacent = s.Adjacent;
                    break;
                }
            }
            if (adjacent is not null)
            {
                break;
            }
        }
        if (adjacent is null)
        {
            return retired;
        }
        if (!projection.ProductionBenches.TryGetValue(out var benches))
        {
            return null;
        }
        // The meal is what the active bills cook; none cooked, no warm spot.
        var (meal, nutrition, cooked) = Pol.PolicyFunctions.ObservedMeal(benches);
        if (!cooked)
        {
            return retired;
        }
        var (perDay, known) = MealsPerDay(projection.FoodSupply, nutrition);
        if (!known)
        {
            return null;
        }
        if (perDay < MealSpotMinPerDay)
        {
            return retired;
        }
        return new Pol.MealStore { Dining = room.Region.Bounds(), Filter = AllowOnly(meal), Anchor = Centroid(adjacent), Avoid = adjacent };
    }

    /// <summary>
    /// The meals the colonists eat a day: their nutrition need over one meal's (the cooked meal's nutrition);
    /// unknown while any colonist's need is.
    /// </summary>
    internal static (double PerDay, bool Known) MealsPerDay(Fact<Pol.FoodSupply> supply, double mealNutrition)
    {
        if (!supply.TryGetValue(out var s))
        {
            return (0, false);
        }
        var total = 0.0;
        foreach (var c in s.Consumers)
        {
            if (!c.NutritionPerDay.TryGetValue(out var need))
            {
                return (0, false);
            }
            total += need;
        }
        return (total / mealNutrition, true);
    }

    internal static bool InRectangle(Pol.Rectangle r, Cell c) =>
        c.X >= r.X && c.X < r.X + r.Width && c.Z >= r.Z && c.Z < r.Z + r.Height;

    internal static Cell Centroid(IReadOnlyList<Cell> cells)
    {
        long sumX = 0, sumZ = 0;
        foreach (var cell in cells)
        {
            sumX += cell.X;
            sumZ += cell.Z;
        }
        return new Cell((int)(sumX / cells.Count), (int)(sumZ / cells.Count));
    }

    /// <summary>
    /// The colony view the storage planner reads: the cells, the layout plan and room census when known, and
    /// the table meal store's spot.
    /// </summary>
    internal static Pol.StoreView StoreView(Observation.ColonyProjection projection, IReadOnlyList<Cell> protectedCells)
    {
        var request = new Pol.StoreView { Bounds = projection.Bounds, Cells = projection.Cells, Protected = protectedCells };
        if (PlannedLayout(projection) is var (plan, rooms, known) && known)
        {
            request = request with { Layout = plan, Rooms = rooms };
        }
        return request with { Shapes = projection.Shapes, Meals = TableMeal(projection) };
    }
}
