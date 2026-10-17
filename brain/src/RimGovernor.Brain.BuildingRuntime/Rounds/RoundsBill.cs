using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Plans the food bills of one purpose (cook, preserve, butcher, cook ahead) through the ledger.</summary>
public sealed partial class RoundsBillPlanner : IPurposeReporter
{
    /// <summary>
    /// The subject keys the purposes' verdicts file under: each Need's bills, a subject of the Need it serves, so
    /// none is a Concern and none collides with the Need's own report.
    /// </summary>
    internal static readonly ConcernId CookingBillsKey = Subject(FoodRules.CookingNeed, "bills");
    internal static readonly ConcernId PreservationBillsKey = Subject(FoodStoresRules.FoodStorageNeed, "bills");
    internal static readonly ConcernId ButcherBillsKey = Subject(FoodRules.FoodSupplyNeed, "bills");
    internal static readonly ConcernId CookAheadBillsKey = Subject(FoodStoresRules.RefrigerationNeed, "bills");

    private static ConcernId Subject(NeedId need, string subject) => NeedId.Of(need.Family, need.Kind, subject).FilingKey;

    private readonly Rounder _reviewer;
    private readonly ConcernId _need;
    private readonly BillPurpose _purpose;
    private readonly object _mu = new();
    private PurposeReport _latest;

    public RoundsBillPlanner(Rounder reviewer, BillPurpose purpose)
    {
        if (reviewer is null || (purpose != ProductionRules.CookFood && purpose != ProductionRules.PreserveFood && purpose != ProductionRules.ButcherFood && purpose != ProductionRules.CookAheadFood))
        {
            throw new ControlException("NewRoundsBillPlanner: reviewer == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood)");
        }
        var need = FoodRules.FoodSupplyNeed.FilingKey;
        if (purpose == ProductionRules.CookFood)
        {
            need = FoodRules.CookingNeed.FilingKey;
        }
        else if (purpose == ProductionRules.PreserveFood)
        {
            need = FoodStoresRules.FoodStorageNeed.FilingKey;
        }
        else if (purpose == ProductionRules.CookAheadFood)
        {
            need = FoodStoresRules.RefrigerationNeed.FilingKey;
        }
        _reviewer = reviewer;
        _purpose = purpose;
        _need = need;
    }

    (ConcernId Key, ConcernId Need) IPurposeReporter.ReportTarget() => ReportTarget();

    PurposeReport IPurposeReporter.Report() => Report();

    internal (ConcernId Key, ConcernId Need) ReportTarget()
    {
        if (_purpose == ProductionRules.CookFood)
        {
            return (CookingBillsKey, _need);
        }
        if (_purpose == ProductionRules.PreserveFood)
        {
            return (PreservationBillsKey, _need);
        }
        if (_purpose == ProductionRules.CookAheadFood)
        {
            return (CookAheadBillsKey, _need);
        }
        return (ButcherBillsKey, _need);
    }

    internal PurposeReport Report()
    {
        lock (_mu)
        {
            return _latest;
        }
    }

    internal void Note(PurposeReport report)
    {
        lock (_mu)
        {
            _latest = report;
        }
    }

    internal PurposeReport FoodReport(FoodOrderRequest request, ColonyProjection projection)
    {
        var order = FoodOrdersRules.SelectFoodOrder(request);
        // Every food bill serves a Need: the Need's standing, not an owner's open work, gates the report.
        var report = _purpose == ProductionRules.CookFood
            ? new PurposeReport { Need = FoodRules.CookingNeedOf(FoodRounds.Facts(projection, _reviewer.Policy)) }
            : _purpose == ProductionRules.PreserveFood
                ? new PurposeReport { Need = FoodStoresRules.FoodStorageNeedOf(FoodStoresFacts.From(projection.Facts), _reviewer.Policy.FoodStorage) }
            : _purpose == ProductionRules.CookAheadFood
                ? new PurposeReport { Need = FoodStoresRules.RefrigerationNeedOf(FoodStoresFacts.From(projection.Facts), _reviewer.Policy.FoodStorage) }
                : new PurposeReport { Need = FoodRules.FoodSupplyNeedOf(FoodRounds.Facts(projection, _reviewer.Policy)) };
        switch (order.Gap)
        {
            case FoodOrderGap.Plan:
                report = report with { Order = AwaitingFoodPlan(order.Subject) };
                return report;
            case FoodOrderGap.Field:
                report = report with { Order = FieldUnavailable(order.Subject) };
                return report;
            case FoodOrderGap.NoDeficit:
                report = report with { Order = BuildingReasonNoDeficit };
                return report;
            case FoodOrderGap.NoBill:
                report = report with { Order = NoBill(projection.ProductionBenches, projection.Facts.Colonists) };
                break;
            default:
                report = report with { Order = WaitFor(Cause.MethodUsed, "ledger_bill") };
                break;
        }
        return report;
    }

    internal FoodOrderRequest FoodRequest(ColonyProjection projection)
    {
        var seasonal = _reviewer.Seasonal(projection.Facts);
        var request = new FoodOrderRequest
        {
            Purpose = _purpose,
            Benches = projection.ProductionBenches,
            Facts = projection.Facts,
            Supply = projection.CombinedFoodSupply,
            Humans = projection.FoodSupply,
            TargetDays = seasonal.FoodTargetDays,
            Warm = Unknown<double>(),
        };
        if (_purpose == ProductionRules.CookFood)
        {
            request = request with { Meals = projection.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays) };
        }
        else if (_purpose == ProductionRules.CookAheadFood)
        {
            var refrigeration = RefrigerationRules.ReviewRefrigeration(projection.Facts.FoodStorageUpkeep, _reviewer.Policy.FoodStorage);
            request = request with { Warm = refrigeration.WarmNutrition };
        }
        return request;
    }

    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(DeclareOrdersAll(snapshot, projection, benches, ct).For(_need.Value));

    internal Declared DeclareOrdersAll(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        try
        {
            // The bills wait on a readable review.
            _ = _reviewer.Player.Journal.LoadRounds(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Note(new PurposeReport { Order = FieldUnavailable("review") });
            return RoundsGearPlanner.AbstainOnRead(Cause.UnreadReview, ex, ct);
        }
        FoodOrderRequest request;
        try
        {
            request = FoodRequest(projection);
        }
        catch (DomainException ex)
        {
            Note(new PurposeReport { Order = FieldUnavailable("food_facts") });
            return RoundsGearPlanner.AbstainOnRead(Cause.UnreadFoodFacts, ex, ct);
        }
        Note(FoodReport(request, projection));
        return FoodOrdersRules.DeclareFoodOrders(request, benches);
    }

    internal Verdict NoBill(Fact<EquatableArray<ProductionBench>> benches, Fact<long> colonists)
    {
        if (!benches.TryGetValue(out var rows))
        {
            return FieldUnavailable("production_benches");
        }
        if (!colonists.IsKnown)
        {
            return FieldUnavailable("colonists");
        }
        var butcher = _purpose == ProductionRules.ButcherFood;
        foreach (var bench in rows)
        {
            if (bench.Usable.TryGetValue(out var usable) && usable && bench.Butcher == butcher)
            {
                return BuildingReasonNoDeficit;
            }
        }
        return butcher ? AwaitingPlan("butcher_bench", "") : AwaitingPlan("cooking_bench", "");
    }
}

partial class RoundsBillPlanner
{
    /// <summary>The verdict a bill gap reads as; throws for a gap outside the closed set.</summary>
    internal static Verdict BillGapVerdict(BillGap gap, string subject) => gap switch
    {
        BillGap.NothingWanted => BuildingReasonNoDeficit,
        BillGap.InProduction => WaitFor(Cause.ExistingWork, subject),
        BillGap.NoRecipe => AwaitingPlan(subject, Wire.Name(gap)),
        BillGap.BenchFull => AwaitingPlan(subject, Wire.Name(gap)),
        BillGap.Waste => AwaitingPlan("mech_waste", ""),
        BillGap.NoCharger => AwaitingPlan("mech_charger", ""),
        _ => throw new InvalidOperationException($"bill gap \"{Wire.Name(gap)}\" is not one of the closed set"),
    };
}
