namespace RimGovernor.Brain.Policy;

// Go: work_dispatch_memory.go.

/// <summary>One Round's reading for the dispatcher.</summary>
public sealed record DispatchInputs
{
    public IReadOnlyList<Declared> Declared { get; init; } = [];

    public IReadOnlyList<GearBench> Benches { get; init; } = [];

    public IReadOnlyList<WorkPawn> Pawns { get; init; } = [];

    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    public long Tick { get; init; }
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
        var (wanted, abstain) = PolicyFunctions.WantedOrders(input.Declared);
        var copies = new Dictionary<string, int>();
        var dispatch = new Dictionary<string, OrderDispatch>();
        foreach (var k in wanted.Keys.Order(StringComparer.Ordinal))
        {
            var order = wanted[k];
            var d = PolicyFunctions.DispatchOrder(order, input.Stock, input.Benches, input.Pawns, Widen.GetValueOrDefault(k));
            dispatch[k] = d;
            copies[k] = d.Copies;
            if (!order.Stocked())
            {
                continue;
            }
            var (window, cal) = PolicyFunctions.CalibrateThroughput(Windows.GetValueOrDefault(k) ?? new ThroughputWindow(), PolicyFunctions.ObserveOrder(order, d, input));
            Windows[k] = window;
            if (cal.Closed && cal.Widen)
            {
                Widen[k] = Widen.GetValueOrDefault(k) + 1;
                Shortfalls.Remove(k);
            }
            else if (cal.Closed && cal.Short)
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
        // Summed in key order: float addition is not associative, and Go's map order is random.
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
        items.AddRange(PolicyFunctions.UnplacedUnmet(unplaced, benches, dispatch));
        return PolicyFunctions.MergeUnmet(items);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Reads one stock-target order off the Round: the benches carrying it, whether its bills are active and a pawn works them, and whether the recipe's ingredients are in stock.</summary>
    public static ThroughputObservation ObserveOrder(OrderSpec order, OrderDispatch d, DispatchInputs input)
    {
        var key = order.Key();
        var o = new ThroughputObservation
        {
            Tick = input.Tick,
            Target = order.Target,
            Eligible = d.Eligible,
            Capacity = d.Capacity,
            Stock = Unknown<long>(),
            Active = true,
            Ingredients = Unknown<bool>(),
        };
        if (input.Stock.TryGetValue(out var have))
        {
            o = o with { Stock = Known(have.GetValueOrDefault(order.Product)) };
        }
        var carrying = new List<string>();
        foreach (var b in input.Benches)
        {
            if (!b.Bills.TryGetValue(out var bills))
            {
                continue;
            }
            foreach (var bill in bills)
            {
                if (bill.Spec.TryGetValue(out var spec) && !bill.Spent && spec.Key() == key)
                {
                    carrying.Add(b.ID);
                    if (!bill.Active.TryGetValue(out var active) || !active)
                    {
                        o = o with { Active = false };
                    }
                }
            }
        }
        o = o with { Carrying = carrying.Count, Busy = BenchesBusy(input.Pawns, carrying) };
        var eligible = EligibleBenches(order, input.Benches);
        if (eligible.Count > 0)
        {
            o = o with { Ingredients = IngredientsStocked(RecipeOf(eligible[0], order.Recipe), input.Stock) };
        }
        return o;
    }

    // Known false when some ingredient slot has every alternative at or below IngredientBoundStock, known true when
    // every slot has one above it, unknown when the recipe's ingredients or the stock are unread.
    private static Fact<bool> IngredientsStocked(GearRecipe r, Fact<IReadOnlyDictionary<Resource, long>> stock)
    {
        var ok = r.Ingredients.TryGetValue(out var slots);
        var haveOk = stock.TryGetValue(out var have);
        if (!ok || !haveOk || slots!.Count == 0)
        {
            return Unknown<bool>();
        }
        foreach (var alternatives in slots)
        {
            var present = false;
            foreach (var a in alternatives)
            {
                if (have!.GetValueOrDefault(a.Resource) > IngredientBoundStock)
                {
                    present = true;
                }
            }
            if (!present)
            {
                return Known(false);
            }
        }
        return Known(true);
    }
}
