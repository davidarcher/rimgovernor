using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>Plans the food bills of one purpose (cook, preserve, butcher, cook ahead) through the ledger.</summary>
public sealed partial class BillPlanner : IPurposeReporter
{
    /// <summary>
    /// The subject keys the purposes' verdicts file under: each Need's bills, a subject of the Need it serves, so
    /// none is a Concern and none collides with the Need's own report.
    /// </summary>
    internal static readonly FilingKey CookingBillsKey = Subject(FoodRules.CookingNeed, "bills");
    internal static readonly FilingKey PreservationBillsKey = Subject(FoodStoresRules.FoodStorageNeed, "bills");
    internal static readonly FilingKey ButcherBillsKey = Subject(FoodRules.FoodSupplyNeed, "bills");
    internal static readonly FilingKey CookAheadBillsKey = Subject(FoodStoresRules.RefrigerationNeed, "bills");

    private static FilingKey Subject(NeedId need, string subject) => NeedId.Of(need.Family, need.Kind, subject).FilingKey;

    private readonly ColonyReview _reviewer;
    private readonly FilingKey _need;
    private readonly BillPurpose _purpose;
    private readonly object _mu = new();
    private PurposeReport _latest;

    public BillPlanner(ColonyReview reviewer, BillPurpose purpose)
    {
        if (reviewer is null || (purpose != ProductionRules.CookFood && purpose != ProductionRules.PreserveFood && purpose != ProductionRules.ButcherFood && purpose != ProductionRules.CookAheadFood))
        {
            throw new ControlException("NewReviewBillPlanner: reviewer == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood)");
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

    (FilingKey Key, FilingKey Need) IPurposeReporter.ReportTarget() => ReportTarget();

    PurposeReport IPurposeReporter.Report() => Report();

    internal (FilingKey Key, FilingKey Need) ReportTarget()
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

    internal PurposeReport FoodReport(FoodOrderRequest request, ColonyReading projection)
    {
        var order = FoodOrdersRules.SelectFoodOrder(request);
        // Every food bill serves a Need: the Need's standing, not an owner's open work, gates the report.
        var report = _purpose == ProductionRules.CookFood
            ? new PurposeReport { Need = FoodRules.CookingNeedOf(FoodReview.Facts(projection, _reviewer.Policy)) }
            : _purpose == ProductionRules.PreserveFood
                ? new PurposeReport { Need = FoodStoresRules.FoodStorageNeedOf(FoodStoresFacts.From(projection.Facts), _reviewer.Policy.FoodStorage) }
            : _purpose == ProductionRules.CookAheadFood
                ? new PurposeReport { Need = FoodStoresRules.RefrigerationNeedOf(FoodStoresFacts.From(projection.Facts), _reviewer.Policy.FoodStorage) }
                : new PurposeReport { Need = FoodRules.FoodSupplyNeedOf(FoodReview.Facts(projection, _reviewer.Policy)) };
        switch (order.Gap)
        {
            case FoodOrderGap.Plan:
                report = report with { Order = AwaitingFoodPlan(order.Subject) };
                return report;
            case FoodOrderGap.NoDeficit:
                report = report with { Order = BuildingReasonNoDeficit };
                return report;
            case FoodOrderGap.NoBill:
                report = report with { Order = NoBill(projection.Facts.EssentialInputs.Benches) };
                break;
            case FoodOrderGap.Chosen:
                report = report with { Order = WaitFor(Cause.MethodUsed, "ledger_bill") };
                break;
        }
        return report;
    }

    internal FoodOrderRequest FoodRequest(ColonyReading projection)
    {
        var seasonal = _reviewer.Seasonal(projection.Facts);
        var request = new FoodOrderRequest
        {
            Purpose = _purpose,
            Benches = projection.Facts.EssentialInputs.Benches,
            Facts = projection.Facts,
            Supply = projection.Facts.CombinedFoodSupply,
            Humans = projection.Facts.FoodSupply,
            TargetDays = seasonal.FoodTargetDays,
        };
        if (_purpose == ProductionRules.CookFood)
        {
            request = request with { Meals = ColonyPlanningRules.MealRequest(projection.Facts, projection.Snapshot.ControlledEnvironment, seasonal.FoodMinDays, seasonal.FoodTargetDays) };
        }
        else if (_purpose == ProductionRules.CookAheadFood)
        {
            var refrigeration = RefrigerationRules.ReviewRefrigeration(projection.Facts.FoodStorageUpkeep, _reviewer.Policy.FoodStorage);
            request = request with { Warm = refrigeration.WarmNutrition };
        }
        return request;
    }

    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(DeclareOrdersAll(snapshot, projection, benches, ct).For(_need.Value));

    internal Declared DeclareOrdersAll(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        try
        {
            // The bills wait on a readable review.
            _ = _reviewer.Player.Journal.LoadReview(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Note(new PurposeReport { Order = FieldUnavailable("review") });
            return GearPlanner.AbstainOnRead(Cause.UnreadReview, ex, ct);
        }
        FoodOrderRequest request;
        try
        {
            request = FoodRequest(projection);
        }
        catch (DomainException ex)
        {
            Note(new PurposeReport { Order = FieldUnavailable("food_facts") });
            return GearPlanner.AbstainOnRead(Cause.UnreadFoodFacts, ex, ct);
        }
        Note(FoodReport(request, projection));
        return FoodOrdersRules.DeclareFoodOrders(request, benches);
    }

    internal Verdict NoBill(EquatableArray<ProductionBench> benches)
    {
        var butcher = _purpose == ProductionRules.ButcherFood;
        foreach (var bench in benches)
        {
            if (bench.Usable && bench.Butcher == butcher)
            {
                return BuildingReasonNoDeficit;
            }
        }
        return butcher ? AwaitingPlan("butcher_bench", "") : AwaitingPlan("cooking_bench", "");
    }
}

partial class BillPlanner
{
    /// <summary>The verdict a bill gap reads as; throws for a gap outside the closed set.</summary>
    internal static Verdict BillGapVerdict(BillGap gap, string subject)
    {
        switch (gap)
        {
            case BillGap.NothingWanted:
                return BuildingReasonNoDeficit;
            case BillGap.InProduction:
                return WaitFor(Cause.ExistingWork, subject);
            case BillGap.NoRecipe:
            case BillGap.BenchFull:
                return AwaitingPlan(subject, Wire.Name(gap));
            case BillGap.Waste:
                return AwaitingPlan("mech_waste", "");
            case BillGap.NoCharger:
                return AwaitingPlan("mech_charger", "");
        }
        throw new DomainException($"undefined bill gap {(int)gap}");
    }
}