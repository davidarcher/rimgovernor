namespace RimGovernor.Brain.Policy;

/// <summary>One stock floor MaintainResource wants a bench to hold.</summary>
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
/// The declaration request of MaintainResource's bench production: the floors, the stock census and the
/// ledger's bench readback.
/// </summary>
public sealed record ResourceOrderRequest
{
    public EquatableArray<ResourceFloor> Floors { get; init; }

    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    public Fact<EquatableArray<GearBench>> Benches { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// MaintainResource's wanted bench orders. A floor below its target declares the first producing recipe's
    /// stock target (the dispatcher sizes how many benches carry it); a bill that already makes the resource
    /// is declared as it stands, which keeps it, so a floor whose stock recovered does not churn its bill.
    /// Bench choice is the ledger's: the order names the bench kind only. A floor with no producing recipe
    /// declares nothing (the planner's mining and sourcing methods take it). Abstain while the stock census,
    /// the bench readback or a bill's identity is unread. Throws <see cref="DomainException"/> on an invalid floor.
    /// </summary>
    public static Declared DeclareResourceOrders(ResourceOrderRequest r)
    {
        var knownStock = r.Stock.TryGetValue(out var stock);
        var benchesKnown = r.Benches.TryGetValue(out var benches);
        if (!knownStock)
        {
            return Abstaining(Cause.UnreadStock);
        }
        if (!benchesKnown)
        {
            return Abstaining(Cause.UnreadBenches);
        }
        var floors = r.Floors.OrderBy(f => f.Resource).ToList();
        var output = new Declared();
        var orders = new List<OrderSpec>();
        foreach (var f in floors)
        {
            if (!ValidResource(f.Resource) || f.Target <= 0 || f.Target > int.MaxValue)
            {
                throw new DomainException($"invalid resource floor \"{f.Resource}\" x{f.Target}");
            }
            var (standing, active, unknown) = StandingResourceBills(benches, f.Resource);
            if (unknown)
            {
                output = output.Unread(Cause.UnreadBills);
                continue;
            }
            if (active || (!f.Reserve && stock!.GetValueOrDefault(f.Resource) >= f.Target))
            {
                orders.AddRange(standing);
                continue;
            }
            var method = SelectResourceMethod(new ResourceMethodRequest { Resource = f.Resource, Target = f.Target, Benches = r.Benches });
            if (method.Kind != ResourceMethodKind.Produce)
            {
                // Nothing to place: an idle bill stands as it is.
                orders.AddRange(standing);
            }
            switch (method.Kind)
            {
                case ResourceMethodKind.Unknown:
                    output = output.Unread(Cause.UnreadRecipes);
                    break;
                case ResourceMethodKind.Produce:
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
                        BenchKind = GearBenchDef(benches, method.Bench),
                        Product = product,
                        Class = @class,
                    });
                    break;
                default:
                    break;
            }
        }
        output.Orders.Clear();
        output.Orders.AddRange(orders);
        return output;
    }

    /// <summary>
    /// The specs of the unspent bills that make resource, and whether one of them is active (still counting
    /// toward its target). unknown is set while a bench's bills or a bill's identity is unread.
    /// </summary>
    internal static (List<OrderSpec> Specs, bool Active, bool Unknown) StandingResourceBills(IReadOnlyList<GearBench> benches, Resource resource)
    {
        var specs = new List<OrderSpec>();
        var active = false;
        foreach (var b in benches)
        {
            if (!b.Bills.TryGetValue(out var bills))
            {
                return ([], false, true);
            }
            foreach (var bill in bills)
            {
                if (bill.Spent || bill.Kind != LedgerBillKind.Production || !ContainsResource(bill.Products, resource))
                {
                    continue;
                }
                var specKnown = bill.Spec.TryGetValue(out var spec);
                var onKnown = bill.Active.TryGetValue(out var on);
                if (!specKnown || !onKnown)
                {
                    return ([], false, true);
                }
                active = active || on;
                specs.Add(spec!);
            }
        }
        return (specs, active, false);
    }
}
