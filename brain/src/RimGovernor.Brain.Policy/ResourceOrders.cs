namespace RimGovernor.Brain.Policy;

/// <summary>One stock floor the economy/resource Need wants a bench to hold.</summary>
public sealed record ResourceFloor
{
    /// <summary>The product a recipe must make: the floor's own resource, or the wort of the beer reserve.</summary>
    public Resource Resource { get; init; }

    public long Target { get; init; }

    /// <summary>
    /// The beer reserve: a BeerReserve bill, wanted whatever the stock, rather than a stock target wanted
    /// only below its floor.
    /// </summary>
    public bool Reserve { get; init; }
}

/// <summary>
/// The declaration request of the economy/resource Need's bench production: the floors, the stock census and the
/// ledger's bench readback.
/// </summary>
public sealed record ResourceOrderRequest
{
    public EquatableArray<ResourceFloor> Floors { get; init; }

    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    public EquatableArray<GearBench> Benches { get; init; }

    /// <summary>The catalog's craft recipes (RoundsFacts.Crafts), ordered with no bench kind when no standing bench makes a floor (#3659).</summary>
    public EquatableArray<GearRecipe> Crafts { get; init; }
}

public static partial class ResourceRules
{
    /// <summary>
    /// the economy/resource Need's wanted bench orders. A floor below its target declares the first producing recipe's
    /// stock target (the dispatcher sizes how many benches carry it); a bill that already makes the resource
    /// is declared as it stands, which keeps it, so a floor whose stock recovered does not churn its bill.
    /// Bench choice is the ledger's: the order names the bench kind only, none for a catalog recipe no bench
    /// stands for (#3659). A floor with no producing recipe declares nothing (the planner's mining and sourcing
    /// methods take it). Abstain while the stock census is unread. Throws <see cref="DomainException"/> on an
    /// invalid floor or bill id.
    /// </summary>
    public static Declared DeclareResourceOrders(ResourceOrderRequest r)
    {
        var knownStock = r.Stock.TryGetValue(out var stock);
        var benches = r.Benches;
        if (!knownStock)
        {
            return WorkRules.Abstaining(Cause.UnreadStock);
        }
        var floors = r.Floors.OrderBy(f => f.Resource).ToList();
        var output = new Declared();
        var orders = new List<OrderSpec>();
        foreach (var f in floors)
        {
            if (!AdmissionRules.ValidResource(f.Resource) || f.Target <= 0 || f.Target > int.MaxValue)
            {
                throw new DomainException($"invalid resource floor \"{f.Resource}\" x{f.Target}");
            }
            var (standing, active) = StandingResourceBills(benches, f.Resource);
            if (active || (!f.Reserve && stock!.GetValueOrDefault(f.Resource) >= f.Target))
            {
                orders.AddRange(standing);
                continue;
            }
            var method = SelectResourceMethod(new ResourceMethodRequest { Resource = f.Resource, Target = f.Target, Benches = r.Benches, Crafts = r.Crafts });
            if (method.Kind != ResourceMethodKind.Produce)
            {
                // Nothing to place: an idle bill stands as it is.
                orders.AddRange(standing);
                continue;
            }
            // An idle bill of another size is replaced, not kept beside it.
            var mode = BillMode.StockTarget;
            var @class = ResourceClass.Material;
            var product = f.Resource;
            if (f.Reserve)
            {
                // The reserve's floor counts beer, not its wort: the rate model has no product to size it by.
                mode = BillMode.BeerReserve;
                @class = ResourceClass.None;
                product = default;
            }
            orders.Add(new OrderSpec
            {
                Recipe = method.Recipe,
                Mode = mode,
                Target = (int)f.Target,
                BenchKind = GearOrdersRules.GearBenchDef(benches, method.Bench),
                Product = product,
                Class = @class,
            });
        }
        output.Orders.Clear();
        output.Orders.AddRange(orders);
        return output;
    }

    /// <summary>
    /// The specs of the unspent bills that make resource, and whether one of them is active (still counting
    /// toward its target).
    /// </summary>
    internal static (List<OrderSpec> Specs, bool Active) StandingResourceBills(IReadOnlyList<GearBench> benches, Resource resource)
    {
        var specs = new List<OrderSpec>();
        var active = false;
        foreach (var b in benches)
        {
            foreach (var bill in b.Bills)
            {
                if (bill.Spent || bill.Kind != LedgerBillKind.Production || !GearRules.ContainsResource(bill.Products, resource))
                {
                    continue;
                }
                active = active || bill.Active;
                specs.Add(bill.Spec);
            }
        }
        return (specs, active);
    }
}
