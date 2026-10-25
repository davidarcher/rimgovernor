namespace RimGovernor.Brain.Policy;

/// <summary>
/// One food a trip carries (#3849), from the catalog: its nutrition a unit, its ThingDef.stackLimit and its ThingDef.VolumePerUnit,
/// which bound how many units one carry holds (Pawn_CarryTracker.MaxStackSpaceEver).
/// </summary>
public sealed record CarriedFood(double Nutrition, int StackLimit, double Volume);

/// <summary>
/// The planned layout's walks of the colony's food (#3849), in cells of the plan's walk (a cardinal step 1, a diagonal step
/// 1.41421 as Pawn.TicksPerMove prices it), each a mean over the cells a carry starts from:
/// <list type="bullet">
/// <item><see cref="FieldToStore"/>: a planned field cell to the nearest store of the harvest (0 when the plan holds no field or
/// no store for it: the harvest lies where it is cut);</item>
/// <item><see cref="SourceToBench"/>: the ingredients' place (the store, else the field) to the nearest bench that cooks the meal;</item>
/// <item><see cref="BenchToDish"/>: the bench to the meal's place (the table's store, else a meal closet, else the store; 0 when the
/// plan holds none: the meal is dropped at the bench).</item>
/// </list>
/// The eater's own walk to its food and on to the table is its meal's time (PawnCycle's eating hours), not hauling.
/// <see cref="Field"/> and <see cref="Bench"/> name what the plan holds. A walk no planned route joins is infinite.
/// </summary>
public sealed record FoodHaulLegs
{
    public bool Field { get; init; }

    public bool Bench { get; init; }

    public double FieldToStore { get; init; }

    public double SourceToBench { get; init; }

    public double BenchToDish { get; init; }
}

public static class FoodHaulRules
{
    /// <summary>A haul job lasts at least this long (JobDriver_HaulToCell.MinimumHaulingJobTicks; kept-constants).</summary>
    public const double HaulJobMinTicks = 30;

    /// <summary>The diagonal step's factor over a cardinal one (Pawn.TicksPerMove).</summary>
    public const double DiagonalFactor = 1.41421;

    /// <summary>The ticks a cell takes at a MoveSpeed of one cell a second (Pawn.TicksPerMove divides the speed by 60).</summary>
    public const double MoveTicksPerSpeed = 60;

    /// <summary>A step's slowest and fastest ticks (Pawn.TicksPerMove's clamp, and its value at no speed).</summary>
    public const double MaxTicksPerCell = 450;

    /// <summary>
    /// The ticks a pawn takes a cell at its MoveSpeed stat (cells a second), as Pawn.TicksPerMove: 60 over the speed, clamped to
    /// 1..450, and 450 at no speed. Terrain's path cost and the weather's slowing of unroofed cells are not priced.
    /// </summary>
    public static double TicksPerCell(double moveSpeed) => moveSpeed > 0 ? Math.Clamp(MoveTicksPerSpeed / moveSpeed, 1, MaxTicksPerCell) : MaxTicksPerCell;

    /// <summary>The units of <paramref name="food"/> one carry holds at <paramref name="capacity"/> (Pawn_CarryTracker.MaxStackSpaceEver).</summary>
    public static double Carry(CarriedFood food, double capacity) =>
        Math.Min(food.StackLimit, Math.Round(capacity / food.Volume, MidpointRounding.ToEven));

    /// <summary>
    /// The plan's food walks (<see cref="FoodHaulLegs"/>) over its route walk (LayoutPlan.RouteWalk: the rooms' interiors, doors and the
    /// open ground around them) and its field zones, a field cell reaching the base through the nearest entrance over open ground.
    /// The harvest's store is a planned cold store (a store_cold piece or a freezer) for a crop that rots, a plain one (a store piece or
    /// a storage room) otherwise, each the other when the plan holds only it; a bench is a work piece of one of
    /// <see cref="FoodWorkFacts.Benches"/>; the table a dine piece.
    /// </summary>
    public static FoodHaulLegs Legs(LayoutPlan plan, FoodWorkFacts food)
    {
        var walk = LayoutPlan.RouteWalk(plan).Walk.Keys.ToHashSet();
        var pieces = plan.Pieces.Values.SelectMany(p => p).ToArray();
        IEnumerable<Cell> Pieces(Func<InteriorPiece, bool> keep) => pieces.Where(keep).SelectMany(p => PlannedRules.RectCells(p.Rect));
        IEnumerable<Cell> Rooms(PlannedRole role) => plan.RoomsOf(role).SelectMany(r => r.Region.Cells());
        HashSet<Cell> Of(IEnumerable<Cell> cells) => cells.Where(walk.Contains).ToHashSet();
        var cold = Of(Pieces(p => p.Row == LayoutGraphRules.NodeId(p.Def, StationPurpose.StoreCold)).Concat(Rooms(PlannedRole.Freezer)));
        var plain = Of(Pieces(p => p.Row == LayoutGraphRules.NodeId(p.Def, StationPurpose.Store)).Concat(Rooms(PlannedRole.Storage)));
        var store = food.HarvestRots ? (cold.Count > 0 ? cold : plain) : (plain.Count > 0 ? plain : cold);
        var benches = Of(Pieces(p => food.Benches.Contains(p.Def) && p.Row == LayoutGraphRules.NodeId(p.Def, StationPurpose.Work)));
        var tables = Of(Pieces(p => p.Row == LayoutGraphRules.NodeId(p.Def, StationPurpose.Dine)));
        var closets = Of(Rooms(PlannedRole.MealCloset));
        var dish = tables.Count > 0 ? tables : closets.Count > 0 ? closets : cold.Count > 0 ? cold : plain;
        var field = plan.Zones.Where(z => z.Kind == LayoutZoneKind.Field).SelectMany(z => z.Runs)
            .SelectMany(r => Enumerable.Range(r.X, r.Length).Select(x => new Cell(x, r.Z))).ToArray();

        // The mean walk of the cells a carry starts from to the nearest of the goals; the field's cells reach the base through an entrance.
        double Mean(IReadOnlyCollection<Cell> from, HashSet<Cell> goals, bool outside)
        {
            if (from.Count == 0 || goals.Count == 0) return 0;
            var dist = Distances(walk, goals);
            var entrances = plan.Entrances.Where(dist.ContainsKey).ToArray();
            return from.Average(c =>
            {
                var best = dist.TryGetValue(c, out var d) ? d : double.PositiveInfinity;
                if (outside) foreach (var e in entrances) best = Math.Min(best, Octile(c, e) + dist[e]);
                return best;
            });
        }

        var source = store.Count > 0 ? (IReadOnlyCollection<Cell>)store : field;
        var outsideSource = store.Count == 0;
        return new FoodHaulLegs
        {
            Field = field.Length > 0, Bench = benches.Count > 0,
            FieldToStore = Mean(field, store, outside: true),
            SourceToBench = Mean(source, benches, outsideSource),
            BenchToDish = Mean(benches, dish, outside: false),
        };
    }

    /// <summary>
    /// The ticks a nutrition eaten costs in hauling (#3849) for a hauler at <paramref name="ticksPerCell"/> and
    /// <paramref name="capacity"/> (its CarryingCapacity stat): per trip the walk to the thing and back with it, the trips the units of a
    /// nutrition fill at one carry each. A haul job (HaulToCell: the harvest to its store, the meal to its place,
    /// FinishRecipeAndStartStoringProduct) lasts at least <see cref="HaulJobMinTicks"/>; the cook's ingredient fetch (JobDriver_DoBill) has
    /// no floor; picking up and putting down take no tick (Toils_Haul.StartCarryThing, PlaceHauledThingInCell). A meal's run makes
    /// <see cref="FoodWorkFacts.DishesPerRun"/> dishes from the ingredients of a run, each fetch carrying what one carry holds. A crop
    /// eaten raw is hauled only to its store. A leg the plan lacks is 0 (<see cref="FoodHaulLegs"/>); without a crop the harvest is not priced.
    /// </summary>
    public static double TicksPerNutrition(FoodWorkFacts food, FoodHaulLegs legs, double ticksPerCell, double capacity)
    {
        double Haul(double cells) => cells > 0 ? Math.Max(HaulJobMinTicks, 2 * cells * ticksPerCell) : 0;
        double Units(CarriedFood thing)
        {
            var carry = Carry(thing, capacity);
            // CarryingCapacity's minValue is 1, so a carry holds at least one unit of every vanilla thing.
            DomainException.Require(carry >= 1 && thing.Nutrition > 0, "carry of food");
            return carry;
        }

        var ticks = 0.0;
        var harvest = food.Harvest;
        if (food.Dish is { } dish)
        {
            var made = food.DishesPerRun * dish.Nutrition;
            DomainException.Require(made > 0, "meal made a run");
            if (harvest is not null)
            {
                var units = food.IngredientPerNutrition / harvest.Nutrition;
                ticks += units / Units(harvest) * Haul(legs.FieldToStore);
                ticks += Math.Ceiling(units * made / Units(harvest)) / made * 2 * legs.SourceToBench * ticksPerCell;
            }

            return ticks + (Math.Ceiling(food.DishesPerRun / Units(dish)) / made * Haul(legs.BenchToDish));
        }

        return harvest is not null ? 1 / (harvest.Nutrition * Units(harvest)) * Haul(legs.FieldToStore) : 0;
    }

    // The octile walk over open ground between two cells.
    private static double Octile(Cell a, Cell b)
    {
        int dx = Math.Abs(a.X - b.X), dz = Math.Abs(a.Z - b.Z);
        return Math.Max(dx, dz) - Math.Min(dx, dz) + (DiagonalFactor * Math.Min(dx, dz));
    }

    // The walk from every cell of walk to the nearest goal: eight steps, a diagonal only past two walkable cells (no corner cut).
    private static Dictionary<Cell, double> Distances(HashSet<Cell> walk, HashSet<Cell> goals)
    {
        var dist = new Dictionary<Cell, double>();
        var queue = new PriorityQueue<Cell, double>();
        foreach (var g in goals)
        {
            dist[g] = 0;
            queue.Enqueue(g, 0);
        }

        while (queue.TryDequeue(out var c, out var d))
        {
            if (d > dist[c]) continue;
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var n = new Cell(c.X + dx, c.Z + dz);
                    if (!walk.Contains(n)) continue;
                    var diagonal = dx != 0 && dz != 0;
                    if (diagonal && (!walk.Contains(new Cell(c.X + dx, c.Z)) || !walk.Contains(new Cell(c.X, c.Z + dz)))) continue;
                    var next = d + (diagonal ? DiagonalFactor : 1);
                    if (dist.TryGetValue(n, out var known) && known <= next) continue;
                    dist[n] = next;
                    queue.Enqueue(n, next);
                }
            }
        }

        return dist;
    }
}
