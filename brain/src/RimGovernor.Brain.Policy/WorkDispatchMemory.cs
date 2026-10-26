namespace RimGovernor.Brain.Policy;


/// <summary>One Round's reading for the dispatcher.</summary>
public sealed record DispatchInputs
{
    public IReadOnlyList<Declared> Declared { get; init; } = [];

    public IReadOnlyList<GearBench> Benches { get; init; } = [];

    public IReadOnlyList<WorkPawn> Pawns { get; init; } = [];

    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    public long Tick { get; init; }

    public RimGovernor.Brain.GameFacts.Observations.DeliveryLedger? Production { get; init; }
}

/// <summary>
/// The dispatcher's calibration state: the rolling windows, the benches added on top of the rate model and the
/// standing shortfalls. It is memory only; a world change empties it.
/// </summary>
public sealed class DispatchMemory
{
    public Dictionary<string, ThroughputWindow> Windows { get; } = [];

    public Dictionary<string, int> Widen { get; } = [];

    public Dictionary<string, UnmetThroughput> Shortfalls { get; } = [];

    /// <summary>
    /// Sizes every declared order (the copies ReconcileLedger wants), then feeds the Round's measurement of each
    /// stock target into its calibration window, widening or recording the shortfall for the next Round. Memory of
    /// an order no planner declares is dropped, unless a planner abstained.
    /// </summary>
    public (IReadOnlyDictionary<string, int> Copies, IReadOnlyDictionary<string, OrderDispatch> Dispatch) Dispatch(DispatchInputs input)
    {
        var (wanted, abstain) = WorkRules.WantedOrders(input.Declared);
        var copies = new Dictionary<string, int>();
        var dispatch = new Dictionary<string, OrderDispatch>();
        foreach (var k in wanted.Keys.Order(StringComparer.Ordinal))
        {
            var order = wanted[k];
            var d = WorkRules.DispatchOrder(order, input.Stock, input.Benches, input.Pawns, Widen.GetValueOrDefault(k));
            dispatch[k] = d;
            copies[k] = d.Copies;
            if (!order.Stocked())
            {
                Windows.Remove(k);
                Widen.Remove(k);
                Shortfalls.Remove(k);
                continue;
            }
            if (d.Sizing is not DispatchSizing.Sized sized)
            {
                // An unsized order has no prediction to calibrate against: its window starts over.
                Windows[k] = new ThroughputWindow();
                Widen.Remove(k);
                Shortfalls.Remove(k);
                continue;
            }
            var previous = Windows.GetValueOrDefault(k) ?? new ThroughputWindow();
            var observation = WorkRules.ObserveOrder(order, d.Eligible, sized, input);
            if (observation.Output is not ProductionOutput.Observed(var token, var units) || previous.LoadToken != token || !WorkRules.SameThroughputBasis(previous.Basis, observation.Basis)
                || observation.Tick < previous.Start || units < previous.StartProduced
                || previous.PerDay != observation.Capacity * observation.Carrying)
            {
                Widen.Remove(k);
                Shortfalls.Remove(k);
                d = WorkRules.DispatchOrder(order, input.Stock, input.Benches, input.Pawns, 0);
                dispatch[k] = d;
                copies[k] = d.Copies;
            }
            var (window, cal) = WorkRules.CalibrateThroughput(previous, observation);
            Windows[k] = window;
            if (cal.Closed && cal.Widen)
            {
                Widen[k] = Widen.GetValueOrDefault(k) + 1;
                Shortfalls.Remove(k);
            }
            else if (cal.Closed && cal.IsShort)
            {
                Shortfalls[k] = new UnmetThroughput { BenchKind = order.BenchKind, ShortPerDay = cal.ShortPerDay, Reason = cal.Reason };
            }
            else if (cal.Closed)
            {
                Shortfalls.Remove(k);
            }
        }
        if (!abstain)
        {
            foreach (var k in Windows.Keys.ToList())
            {
                if (!wanted.ContainsKey(k))
                {
                    Windows.Remove(k);
                    Widen.Remove(k);
                    Shortfalls.Remove(k);
                }
            }
        }
        return (copies, dispatch);
    }

    /// <summary>
    /// The Round's unmet throughput per bench kind: the standing calibration shortfalls, the orders the rate model
    /// wants more benches for than exist and the orders no bench could take (unplaced).
    /// </summary>
    public IReadOnlyList<UnmetThroughput> Unmet(
        IReadOnlyDictionary<string, OrderDispatch> dispatch,
        IReadOnlyDictionary<string, OrderSpec> wanted,
        IReadOnlyList<OrderSpec> unplaced,
        IReadOnlyList<GearBench> benches)
    {
        var items = new List<UnmetThroughput>();
        // Summed in key order: float addition is not associative, and a map's order is not fixed.
        foreach (var k in Shortfalls.Keys.Order(StringComparer.Ordinal))
        {
            items.Add(Shortfalls[k]);
        }
        foreach (var k in dispatch.Keys.Order(StringComparer.Ordinal))
        {
            if (dispatch[k].NoBenchShort > 0)
            {
                items.Add(new UnmetThroughput { BenchKind = wanted[k].BenchKind, ShortPerDay = dispatch[k].NoBenchShort, Reason = UnmetReason.NoBench });
            }
        }
        items.AddRange(WorkRules.UnplacedUnmet(unplaced, benches, dispatch));
        return WorkRules.MergeUnmet(items);
    }
}

public static partial class WorkRules
{
    /// <summary>Reads one stock-target order off the Round: the benches carrying it, whether its bills are active and a pawn works them, and whether the recipe's ingredients are in stock.</summary>
    public static ThroughputObservation ObserveOrder(OrderSpec order, int eligibleCount, DispatchSizing.Sized sized, DispatchInputs input)
    {
        var key = order.Key();
        var o = new ThroughputObservation
        {
            Tick = input.Tick,
            Target = order.Target,
            Eligible = eligibleCount,
            Capacity = sized.Capacity,
            Stock = sized.Stock,
            Active = true,
            Ingredients = ThroughputIngredients.NoRecipe,
        };
        var carrying = new List<string>();
        var bills = new List<string>();
        foreach (var b in input.Benches)
        {
            foreach (var bill in b.Bills)
            {
                if (!bill.Spent && bill.Spec.Key() == key)
                {
                    carrying.Add(b.Id);
                    bills.Add(bill.Id);
                    if (!bill.Active)
                    {
                        o = o with { Active = false };
                    }
                }
            }
        }
        o = o with { Carrying = carrying.Distinct(StringComparer.Ordinal).Count(), Busy = BenchesBusy(input.Pawns, carrying) };
        o = o with
        {
            Output = input.Production is { } production ? new ProductionOutput.Observed(production.LoadToken, production.Counts
                .Where(c => c.Kind == RimGovernor.Brain.GameFacts.Observations.DeliveryKind.Recipe
                    && c.Def == order.Product.Value && bills.Contains(c.Source, StringComparer.Ordinal)).Sum(c => c.Units))
                : new ProductionOutput.CensusUnread(),
            Basis = new(order,
                EquatableArray.From(EligibleBenches(order, input.Benches)
                    .Select(b => new ThroughputBench(b.Id, b.Usable, b.WorkSpeed, RecipeOf(b, order.Recipe), EquatableArray.From(b.Bills
                        .Where(bill => bills.Contains(bill.Id, StringComparer.Ordinal)).OrderBy(bill => bill.Id, StringComparer.Ordinal)
                        .Select(bill => new ThroughputBill(bill.Id, bill.Active, bill.Worker)))))),
                EquatableArray.From(input.Pawns.OrderBy(p => p.Id).Select(p => new ThroughputWorker(p.Id, p.Available, p.Applies,
                    EquatableArray.From(p.Work.Priorities()), p.WorkSpeeds, p.Skills))))
        };
        var eligible = EligibleBenches(order, input.Benches);
        if (eligible.Count > 0)
        {
            o = o with { Ingredients = IngredientsStocked(RecipeOf(eligible[0], order.Recipe), input.Stock) };
        }
        return o;
    }

    // Missing when some ingredient slot has every alternative at or below IngredientBoundStock, stocked when
    // every slot has one above it, including recipes taking none; NoRecipe when no bench offers the recipe.
    private static ThroughputIngredients IngredientsStocked(GearRecipe? r, IReadOnlyDictionary<Resource, long> have)
    {
        if (r is null)
        {
            return ThroughputIngredients.NoRecipe;
        }
        foreach (var alternatives in r.Ingredients)
        {
            var present = false;
            foreach (var a in alternatives)
            {
                if (have.GetValueOrDefault(a.Resource) > IngredientBoundStock)
                {
                    present = true;
                }
            }
            if (!present)
            {
                return ThroughputIngredients.Missing;
            }
        }
        return ThroughputIngredients.Stocked;
    }
}
