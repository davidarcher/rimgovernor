namespace RimGovernor.Brain.Policy;

// Go: trade_mission_purchase.go.

/// <summary>Holds a food mission until its owning review measures demand. It is not a known empty purchase plan or a return decision.</summary>
public sealed class TradeMissionFoodDemandUnknownException() : DomainException("trade mission food demand unknown");

/// <summary>Decoded catalog facts for a settlement. Unknown compatibility or nutrition cannot authorize a food cargo choice.</summary>
public sealed record MissionSupplyDefinition
{
    public string Definition { get; init; } = "";

    public Fact<bool> Prepared { get; init; }

    public Fact<double> Nutrition { get; init; }

    public Fact<bool> CanSupply { get; init; }
}

/// <summary>The current native mass, provisions and exact home route.</summary>
public sealed record MissionReturnFacts
{
    public Fact<double> Mass { get; init; }

    public Fact<double> Capacity { get; init; }

    public Fact<double> FoodDays { get; init; }

    public Fact<double> RotDays { get; init; }

    public Fact<long> RouteTicks { get; init; }
}

/// <summary>A live native offer with decoded catalog mass.</summary>
public sealed record MissionPurchaseRow
{
    public string LineID { get; init; } = "";

    public string DefName { get; init; } = "";

    public long TraderCount { get; init; }

    public bool Pawn { get; init; }

    public bool Currency { get; init; }

    public Fact<double> BuyPrice { get; init; }

    public Fact<double> UnitMass { get; init; }

    public Fact<bool> WillTrade { get; init; }
}

public sealed record MissionPurchaseRequest
{
    public required TradeMission Mission { get; init; }

    public IReadOnlyDictionary<Resource, long> Targets { get; init; } = new Dictionary<Resource, long>();

    public Fact<IReadOnlyList<Amount>> Stock { get; init; }

    public Fact<FoodPlan> Food { get; init; }

    public double FoodTargetDays { get; init; }

    public IReadOnlyDictionary<string, Fact<double>> Nutrition { get; init; } = new Dictionary<string, Fact<double>>();

    public IReadOnlyList<MissionPurchaseRow> Rows { get; init; } = [];

    public Fact<long> Silver { get; init; }

    public Fact<double> Mass { get; init; }

    public Fact<double> Capacity { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Sizes saved authorization from the owning supply gaps.</summary>
    public static (IReadOnlyList<CargoItem> Cargo, IReadOnlyList<ResourceKey> Goods) TradeMissionCargo(
        IReadOnlyList<SupplyDemandResult> demands,
        IReadOnlyDictionary<ResourceKey, Fact<bool>> compatible,
        IReadOnlyList<MissionSupplyDefinition> definitions,
        double foodTargetDays
    )
    {
        var ordered = definitions.OrderBy(d => d.Definition, StringComparer.Ordinal).ToList();
        var cargo = new List<CargoItem>();
        var goods = new List<ResourceKey>();
        foreach (var d in demands)
        {
            var known = compatible.GetValueOrDefault(d.Demand.Good).TryGetValue(out var can);
            if (!FoodNumber(d.Gap) || d.Gap <= 0 || !known || !can)
            {
                continue;
            }
            var def = d.Demand.Good.Def.Value;
            var count = Math.Ceiling(d.Gap);
            if (d.Demand.Good == NutritionKey)
            {
                def = "";
                foreach (var row in ordered)
                {
                    var pk = row.Prepared.TryGetValue(out var prepared);
                    var nk = row.Nutrition.TryGetValue(out var nutrition);
                    var sk = row.CanSupply.TryGetValue(out var supply);
                    if (pk && prepared && nk && FoodNumber(nutrition) && nutrition > 0 && sk && supply)
                    {
                        def = row.Definition;
                        count = Math.Ceiling(d.Gap * Math.Max(1, foodTargetDays) / nutrition);
                        break;
                    }
                }
            }
            if (def.Length == 0 || !FoodNumber(count) || count <= 0 || count > int.MaxValue || cargo.Any(item => item.Definition == def))
            {
                continue;
            }
            cargo.Add(new CargoItem(def, (ulong)count));
            goods.Add(d.Demand.Good);
        }
        return (cargo, goods);
    }

    /// <summary>Retains the shared colony silver floor before departure.</summary>
    public static Fact<long> TradeMissionBudget(Fact<long> silver, Fact<long> colonists)
    {
        var known = silver.TryGetValue(out var amount);
        var (reserve, rk) = TradeSilverReserve(colonists);
        if (!known || !rk)
        {
            return Unknown<long>();
        }
        return Known(Math.Min(Math.Max(0, amount - reserve), int.MaxValue));
    }

    public static bool TradeMissionReturnSafe(MissionReturnFacts r)
    {
        var mk = r.Mass.TryGetValue(out var mass);
        var ck = r.Capacity.TryGetValue(out var capacity);
        var fk = r.FoodDays.TryGetValue(out var food);
        var rk = r.RotDays.TryGetValue(out var rot);
        var tk = r.RouteTicks.TryGetValue(out var ticks);
        if (!mk || !ck || !fk || !rk || !tk || ticks < 0 || !FoodNumber(mass) || !FoodNumber(capacity) || !FoodNumber(food) || !FoodNumber(rot) || mass > capacity)
        {
            return false;
        }
        var days = (double)ticks / GameTime.TicksPerDay;
        return food >= days && rot >= days;
    }

    /// <summary>Bounds live demand by saved authorization, silver and mass. Throws <see cref="TradeMissionFoodDemandUnknownException"/> while a food demand is unmeasured.</summary>
    public static (IReadOnlyList<TradeLine> Lines, IReadOnlyList<CargoItem> Cargo) PlanTradeMissionPurchases(MissionPurchaseRequest request)
    {
        IReadOnlyList<TradeLine> noLines = [];
        IReadOnlyList<CargoItem> noCargo = [];
        var m = request.Mission;
        var foodKnown = request.Food.TryGetValue(out var food);
        foreach (var item in m.Demand)
        {
            if (request.Nutrition.GetValueOrDefault(item.Definition).TryGetValue(out var nutrition) && nutrition > 0 && !foodKnown)
            {
                throw new TradeMissionFoodDemandUnknownException();
            }
        }
        var current = new Dictionary<string, long>(StringComparer.Ordinal);
        var known = request.Stock.TryGetValue(out var stocks);
        var quantities = new List<ResourceQuantity>();
        foreach (var stock in stocks ?? [])
        {
            quantities.Add(new ResourceQuantity { Key = new ResourceKey { Def = stock.Resource }, Count = stock.Count });
        }
        var resourceTargets = new List<ResourceDemand>();
        foreach (var (def, target) in request.Targets)
        {
            resourceTargets.Add(new ResourceDemand { Key = new ResourceKey { Def = def }, Count = target, Priority = 1 });
        }
        var stockFact = known ? Known(EquatableArray.From(quantities)) : Unknown<EquatableArray<ResourceQuantity>>();
        var demand = BuildResourceDemand(new ResourceDemandInput { Targets = EquatableArray.From(resourceTargets), Stock = stockFact });
        if (!demand.TryGetValue(out var needs))
        {
            return (noLines, noCargo);
        }
        foreach (var need in needs)
        {
            current[need.Key.Def.Value] = need.Count;
        }
        var demands = new List<SupplyDemand>();
        var candidates = new List<SupplyCandidate>();
        var rows = new Dictionary<string, MissionPurchaseRow>(StringComparer.Ordinal);
        double budget = m.SilverBudget;
        var mk = request.Mass.TryGetValue(out var mass);
        var ck = request.Capacity.TryGetValue(out var capacity);
        if (!mk || !ck || !FoodNumber(mass) || !FoodNumber(capacity) || mass > capacity)
        {
            return (noLines, noCargo);
        }
        var headroom = capacity - mass;
        if (!request.Silver.TryGetValue(out var silver))
        {
            return (noLines, noCargo);
        }
        budget = Math.Min(budget, silver);
        var wanted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in m.Demand)
        {
            var count = Math.Min((long)item.Count, current.GetValueOrDefault(item.Definition));
            if (request.Nutrition.GetValueOrDefault(item.Definition).TryGetValue(out var nutrition) && nutrition > 0)
            {
                var foodWanted = Math.Max(0, food!.GapPerDay) * Math.Max(1, request.FoodTargetDays);
                count = Math.Min((long)item.Count, (long)Math.Ceiling(foodWanted / nutrition));
            }
            if (count > 0)
            {
                wanted[item.Definition] = count;
                demands.Add(new SupplyDemand { Good = new ResourceKey { Def = new Resource(item.Definition) }, Units = count, Priority = 1 });
            }
        }
        foreach (var row in request.Rows)
        {
            var pk = row.BuyPrice.TryGetValue(out var price);
            var wk = row.WillTrade.TryGetValue(out var willTrade);
            if (wanted.GetValueOrDefault(row.DefName) == 0 || row.Pawn || row.Currency || !pk || price <= 0 || !FoodNumber(price) || !wk || !willTrade)
            {
                continue;
            }
            var count = Math.Min(Math.Min(wanted[row.DefName], row.TraderCount), (long)Math.Floor(budget / price));
            if (!row.UnitMass.TryGetValue(out var unitMass) || !FoodNumber(unitMass))
            {
                continue;
            }
            if (unitMass > 0)
            {
                count = Math.Min(count, (long)Math.Floor(headroom / unitMass));
            }
            if (count <= 0)
            {
                continue;
            }
            if (TradeCandidate(new Resource(row.DefName), row.LineID, count, price, out var candidate))
            {
                candidates.Add(candidate);
                rows[candidate.ID] = row;
                budget -= count * price;
                headroom -= count * unitMass;
                wanted[row.DefName] -= count;
            }
        }
        var plan = PlanSupply(
            new SupplyPlanRequest
            {
                Demands = Known<IReadOnlyList<SupplyDemand>>(demands),
                Candidates = Known<IReadOnlyList<SupplyCandidate>>(candidates),
                Labor = Known(m.SilverBudget * TradeLaborPerSilver),
            }
        );
        var lines = new List<TradeLine>();
        var goods = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var entry in plan.Portfolio)
        {
            if (entry.Decision != SupplyDecision.Open || entry.Wanted <= 0)
            {
                continue;
            }
            var row = rows[entry.Candidate.ID];
            lines.Add(new TradeLine(row.LineID, (int)entry.Wanted));
            goods[row.DefName] = goods.GetValueOrDefault(row.DefName) + (ulong)entry.Wanted;
        }
        var cargo = goods.Select(kv => new CargoItem(kv.Key, kv.Value)).OrderBy(c => c.Definition, StringComparer.Ordinal).ToList();
        return (lines, cargo);
    }

    /// <summary>Rechecks the staged deal against saved authorization.</summary>
    public static bool TradeMissionPaymentSafe(long budget, Fact<long> silver, Fact<double> balance)
    {
        var sk = silver.TryGetValue(out var amount);
        var bk = balance.TryGetValue(out var charge);
        return sk && bk && FoodNumber(-charge) && -charge <= budget && amount + charge >= 0;
    }
}
