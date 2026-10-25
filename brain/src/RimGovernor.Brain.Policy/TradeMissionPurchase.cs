namespace RimGovernor.Brain.Policy;


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

    public IReadOnlyList<Resource> Medicines { get; init; } = [];

    public IReadOnlyList<MissionPurchaseRow> Rows { get; init; } = [];

    public long Silver { get; init; }

    public double Mass { get; init; }

    public double Capacity { get; init; }
}

public static partial class TradePolicyRules
{
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

    /// <summary>Buys exactly the saved selected-wait authorization, bounded by live silver and carry capacity.</summary>
    public static (IReadOnlyList<TradeLine> Lines, IReadOnlyList<CargoItem> Cargo) PlanTradeMissionPurchases(MissionPurchaseRequest request)
    {
        if (!double.IsFinite(request.Mass) || !double.IsFinite(request.Capacity) || request.Mass > request.Capacity) return ([], []);
        var needs = request.Mission.Demand.Select(d => new StockNeed([new Resource(d.Definition)], (long)d.Count)).ToArray();
        var offers = request.Rows.Where(r => !r.Pawn && !r.Currency && r.WillTrade && r.TraderCount > 0)
            .Select(r => new StockPurchaseOffer(r.LineID, new Resource(r.DefName), r.TraderCount, r.BuyPrice, r.UnitMass));
        var purchases = TradeStockPurchases.Plan(needs, request.Medicines, offers, Math.Min(request.Mission.SilverBudget, request.Silver), request.Capacity - request.Mass);
        return (purchases.Select(p => new TradeLine(p.Id, checked((int)p.Count))).ToArray(), purchases.GroupBy(p => p.Resource)
            .Select(g => new CargoItem(g.Key.Value, checked((ulong)g.Sum(p => p.Count)))).OrderBy(c => c.Definition, StringComparer.Ordinal).ToArray());
    }
    /// <summary>Rechecks the staged deal against saved authorization.</summary>
    public static bool TradeMissionPaymentSafe(long budget, long silver, double charge) =>
        FoodForecastRules.FoodNumber(-charge) && -charge <= budget && silver + charge >= 0;
}
