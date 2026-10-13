namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The gear-bench census read of the art, surgery and bill planners.</summary>
public interface IArtBenchSource
{
    Task<(IReadOnlyList<GearBenchRead> Reads, BridgeResult Result)> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);
}

/// <summary>Plans the food bills of one purpose (cook, preserve, butcher, cook ahead) through the ledger.</summary>
public sealed class RoundsBillPlanner : IPurposeReporter
{
    internal static readonly ConcernId CookingBillsKey = new("cookingBills");
    internal static readonly ConcernId PreservationBillsKey = new("preservationBills");
    internal static readonly ConcernId ButcherBillsKey = new("butcherBills");
    internal static readonly ConcernId CookAheadBillsKey = new("cookAheadBills");

    private readonly Rounder _reviewer;
    private readonly ConcernId _need;
    private readonly BillPurpose _purpose;
    private readonly object _mu = new();
    private PurposeReport _latest;

    public RoundsBillPlanner(Rounder reviewer, BillPurpose purpose)
    {
        if (reviewer is null || (purpose != PolicyConstants.CookFood && purpose != PolicyConstants.PreserveFood && purpose != PolicyConstants.ButcherFood && purpose != PolicyConstants.CookAheadFood))
        {
            throw new ControlException("NewRoundsBillPlanner: reviewer == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood)");
        }
        var need = PolicyApi.EnsureFoodSupply;
        if (purpose == PolicyConstants.CookFood)
        {
            need = PolicyApi.EnsureCooking;
        }
        else if (purpose == PolicyConstants.PreserveFood)
        {
            need = PolicyApi.MaintainFoodStorage;
        }
        else if (purpose == PolicyConstants.CookAheadFood)
        {
            need = PolicyApi.MaintainRefrigeration;
        }
        _reviewer = reviewer;
        _purpose = purpose;
        _need = need;
    }

    (ConcernId Key, ConcernId Need) IPurposeReporter.ReportTarget() => ReportTarget();

    PurposeReport IPurposeReporter.Report() => Report();

    internal (ConcernId Key, ConcernId Need) ReportTarget()
    {
        if (_purpose == PolicyConstants.CookFood)
        {
            return (CookingBillsKey, _need);
        }
        if (_purpose == PolicyConstants.PreserveFood)
        {
            return (PreservationBillsKey, _need);
        }
        if (_purpose == PolicyConstants.CookAheadFood)
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

    /// <summary>Whether an owner's plans hold open bill work of this purpose.</summary>
    internal bool OpenWork(StoreJournal journal, StandardState owner, CancellationToken ct)
    {
        foreach (var method in owner.Methods)
        {
            var plan = journal.LoadPlan(method.Plan, ct);
            if (_need == PolicyApi.EnsureFoodSupply)
            {
                foreach (var progress in plan.Progress)
                {
                    if (progress.Action.Kind == ActionKind.ProductionBill && Standard.WorkOpen([progress]))
                    {
                        if (progress.Action.ProductionBill is { } b && _purpose == PolicyConstants.ButcherFood && b.Mode == BillMode.ButcherForever)
                        {
                            continue;
                        }
                        return true;
                    }
                }
                continue;
            }
            if (_purpose != PolicyConstants.CookFood && StorePackage.PlanOpen(plan))
            {
                return true;
            }
        }
        return false;
    }

    internal PurposeReport FoodReport(FoodOrderRequest request, ColonyProjection projection)
    {
        var order = PolicyApi.SelectFoodOrder(request);
        var report = new PurposeReport { OpenWork = OpenWork };
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
        if (order.ReserveRunning)
        {
            report = report with { WorkTicks = ReserveBillWorkTicks };
        }
        return report;
    }

    internal FoodOrderRequest FoodRequest(ColonyProjection projection, RoundsReview review)
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
        if (_purpose == PolicyConstants.CookFood)
        {
            request = request with { Meals = projection.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays) };
        }
        else if (_purpose == PolicyConstants.CookAheadFood)
        {
            var refrigeration = PolicyApi.ReviewRefrigeration(projection.Facts.FoodStorageUpkeep, review.Latches.Refrigeration, _reviewer.Policy.FoodStorage);
            request = request with { Warm = refrigeration.WarmNutrition };
        }
        return request;
    }

    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(DeclareOrdersAll(snapshot, projection, benches, ct).For(_need));

    internal Declared DeclareOrdersAll(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        RoundsReview review;
        try
        {
            review = _reviewer.Player.Journal.LoadRounds(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Note(new PurposeReport { Order = FieldUnavailable("review") });
            return AbstainOnRead(Cause.UnreadReview, ex, ct);
        }
        FoodOrderRequest request;
        try
        {
            request = FoodRequest(projection, review);
        }
        catch (DomainException ex)
        {
            Note(new PurposeReport { Order = FieldUnavailable("food_facts") });
            return AbstainOnRead(Cause.UnreadFoodFacts, ex, ct);
        }
        Note(FoodReport(request, projection));
        return PolicyApi.DeclareFoodOrders(request, benches);
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
        var butcher = _purpose == PolicyConstants.ButcherFood;
        foreach (var bench in rows)
        {
            if (bench.Usable.TryGetValue(out var usable) && usable && bench.Butcher == butcher)
            {
                return BuildingReasonNoDeficit;
            }
        }
        return butcher ? AwaitingPlan("butcher_bench", "") : AwaitingPlan("cooking_bench", "");
    }

    internal const int ReserveBillWorkTicks = GameTime.TicksPerHour;
}

internal static partial class BuildingRuntimePackage
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
