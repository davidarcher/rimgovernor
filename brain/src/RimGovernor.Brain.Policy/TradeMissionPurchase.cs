namespace RimGovernor.Brain.Policy;


/// <summary>A prepared food a settlement's trader kind supplies, with the nutrition the game shows for it.</summary>
public sealed record MissionSupplyDefinition
{
    public string Definition { get; init; } = "";

    public double Nutrition { get; init; }
}

/// <summary>The current native mass, provisions and exact home route.</summary>
public sealed record MissionReturnFacts
{
    public double Mass { get; init; }

    public double Capacity { get; init; }

    public double FoodDays { get; init; }

    public double RotDays { get; init; }

    public long RouteTicks { get; init; }
}

/// <summary>A live native offer with its catalog unit mass.</summary>
public sealed record MissionPurchaseRow
{
    public string LineID { get; init; } = "";

    public string DefName { get; init; } = "";

    public long TraderCount { get; init; }

    public bool Pawn { get; init; }

    public bool Currency { get; init; }

    public double BuyPrice { get; init; }

    public double UnitMass { get; init; }

    public bool WillTrade { get; init; }
}

public sealed record MissionPurchaseRequest
{
    public required TradeMission Mission { get; init; }

    public IReadOnlyDictionary<Resource, long> Targets { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyList<Amount> Stock { get; init; } = [];

    /// <summary>The food plan's nutrition gap per day; the caller requires the plan when the mission carries food.</summary>
    public double FoodGapPerDay { get; init; }

    public double FoodTargetDays { get; init; }

    /// <summary>The shown nutrition of each demanded definition that has one.</summary>
    public IReadOnlyDictionary<string, double> Nutrition { get; init; } = new Dictionary<string, double>();

    public IReadOnlyList<MissionPurchaseRow> Rows { get; init; } = [];

    public long Silver { get; init; }

    public double Mass { get; init; }

    public double Capacity { get; init; }
}

public static partial class TradePolicyRules
{
    /// <summary>Sizes saved authorization from the owning supply gaps; compatible names the goods the settlement supplies.</summary>
    public static (IReadOnlyList<CargoItem> Cargo, IReadOnlyList<ResourceKey> Goods) TradeMissionCargo(
        IReadOnlyList<SupplyDemandResult> demands,
        IReadOnlySet<ResourceKey> compatible,
        IReadOnlyList<MissionSupplyDefinition> definitions,
        double foodTargetDays
    )
    {
        var ordered = definitions.OrderBy(d => d.Definition, StringComparer.Ordinal).ToList();
        var cargo = new List<CargoItem>();
        var goods = new List<ResourceKey>();
        foreach (var d in demands)
        {
            if (!FoodForecastRules.FoodNumber(d.Gap) || d.Gap <= 0 || !compatible.Contains(d.Demand.Good))
            {
                continue;
            }
            var def = d.Demand.Good.Def.Value;
            var count = Math.Ceiling(d.Gap);
            if (d.Demand.Good == SupplyPlanRules.NutritionKey)
            {
                def = "";
                foreach (var row in ordered)
                {
                    if (FoodForecastRules.FoodNumber(row.Nutrition) && row.Nutrition > 0)
                    {
                        def = row.Definition;
                        count = Math.Ceiling(d.Gap * Math.Max(1, foodTargetDays) / row.Nutrition);
                        break;
                    }
                }
            }
            if (def.Length == 0 || !FoodForecastRules.FoodNumber(count) || count <= 0 || count > int.MaxValue || cargo.Any(item => item.Definition == def))
            {
                continue;
            }
            cargo.Add(new CargoItem(def, (ulong)count));
            goods.Add(d.Demand.Good);
        }
        return (cargo, goods);
    }

    /// <summary>Retains the shared colony silver floor before departure; zero when the colony size prices no floor.</summary>
    public static long TradeMissionBudget(long silver, long colonists)
    {
        var (reserve, rk) = TradeSilverReserve(colonists);
        return rk ? Math.Min(Math.Max(0, silver - reserve), int.MaxValue) : 0;
    }

    public static bool TradeMissionReturnSafe(MissionReturnFacts r)
    {
        if (r.RouteTicks < 0 || !FoodForecastRules.FoodNumber(r.Mass) || !FoodForecastRules.FoodNumber(r.Capacity) || !FoodForecastRules.FoodNumber(r.FoodDays) || !FoodForecastRules.FoodNumber(r.RotDays) || r.Mass > r.Capacity)
        {
            return false;
        }
        var days = (double)r.RouteTicks / GameTime.TicksPerDay;
        return r.FoodDays >= days && r.RotDays >= days;
    }

    /// <summary>Bounds live demand by saved authorization, silver and mass.</summary>
    public static (IReadOnlyList<TradeLine> Lines, IReadOnlyList<CargoItem> Cargo) PlanTradeMissionPurchases(MissionPurchaseRequest request)
    {
        IReadOnlyList<TradeLine> noLines = [];
        IReadOnlyList<CargoItem> noCargo = [];
        var m = request.Mission;
        if (!FoodForecastRules.FoodNumber(request.Mass) || !FoodForecastRules.FoodNumber(request.Capacity) || request.Mass > request.Capacity)
        {
            return (noLines, noCargo);
        }
        // The resource shortfall: each target less the colony's stock of its definition.
        var held = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var stock in request.Stock)
        {
            held[stock.Resource.Value] = held.GetValueOrDefault(stock.Resource.Value) + stock.Count;
        }
        var current = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (def, target) in request.Targets)
        {
            current[def.Value] = Math.Max(0, target - held.GetValueOrDefault(def.Value));
        }
        var demands = new List<SupplyDemand>();
        var candidates = new List<SupplyCandidate>();
        var rows = new Dictionary<string, MissionPurchaseRow>(StringComparer.Ordinal);
        var headroom = request.Capacity - request.Mass;
        var budget = Math.Min((double)m.SilverBudget, request.Silver);
        var wanted = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in m.Demand)
        {
            var count = Math.Min((long)item.Count, current.GetValueOrDefault(item.Definition));
            if (request.Nutrition.TryGetValue(item.Definition, out var nutrition) && nutrition > 0)
            {
                var foodWanted = Math.Max(0, request.FoodGapPerDay) * Math.Max(1, request.FoodTargetDays);
                count = Math.Min((long)item.Count, (long)Math.Ceiling(foodWanted / nutrition));
            }
            if (count > 0)
            {
                wanted[item.Definition] = count;
                demands.Add(new SupplyDemand { Good = new ResourceKey { Def = new Resource(item.Definition) }, Units = count });
            }
        }
        foreach (var row in request.Rows)
        {
            var price = row.BuyPrice;
            if (wanted.GetValueOrDefault(row.DefName) == 0 || row.Pawn || row.Currency || price <= 0 || !FoodForecastRules.FoodNumber(price) || !row.WillTrade || !FoodForecastRules.FoodNumber(row.UnitMass))
            {
                continue;
            }
            var count = Math.Min(Math.Min(wanted[row.DefName], row.TraderCount), (long)Math.Floor(budget / price));
            if (row.UnitMass > 0)
            {
                count = Math.Min(count, (long)Math.Floor(headroom / row.UnitMass));
            }
            if (count <= 0)
            {
                continue;
            }
            if (AcquisitionCatalogRules.TradeCandidate(new Resource(row.DefName), row.LineID, count, price, out var candidate))
            {
                candidates.Add(candidate);
                rows[candidate.Id] = row;
                budget -= count * price;
                headroom -= count * row.UnitMass;
                wanted[row.DefName] -= count;
            }
        }
        var plan = SupplyPlanRules.PlanSupply(
            new SupplyPlanRequest
            {
                Demands = EquatableArray.From(demands),
                Candidates = EquatableArray.From(candidates),
                Labor = m.SilverBudget * AcquisitionCatalogRules.TradeLaborPerSilver,
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
            var row = rows[entry.Candidate.Id];
            lines.Add(new TradeLine(row.LineID, (int)entry.Wanted));
            goods[row.DefName] = goods.GetValueOrDefault(row.DefName) + (ulong)entry.Wanted;
        }
        var cargo = goods.Select(kv => new CargoItem(kv.Key, kv.Value)).OrderBy(c => c.Definition, StringComparer.Ordinal).ToList();
        return (lines, cargo);
    }

    /// <summary>Rechecks the staged deal against saved authorization.</summary>
    public static bool TradeMissionPaymentSafe(long budget, long silver, double charge) =>
        FoodForecastRules.FoodNumber(-charge) && -charge <= budget && silver + charge >= 0;
}
