namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The gear-bench census read of the art, surgery and bill planners.</summary>
public interface IArtBenchSource
{
    (IReadOnlyList<GearBenchRead> Reads, BridgeResult Result) ReadGearBenches(C.Identity identity, CancellationToken ct);
}

/// <summary>Plans the food bills of one purpose (cook, preserve, butcher, cook ahead) through the ledger.</summary>
public sealed class RoundsBillPlanner
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
        if (reviewer is null || (purpose != PolicyApi.CookFood && purpose != PolicyApi.PreserveFood && purpose != PolicyApi.ButcherFood && purpose != PolicyApi.CookAheadFood))
        {
            throw new ControlException("NewRoundsBillPlanner: reviewer == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood)");
        }
        var need = PolicyApi.EnsureFoodSupply;
        if (purpose == PolicyApi.CookFood)
        {
            need = PolicyApi.EnsureCooking;
        }
        else if (purpose == PolicyApi.PreserveFood)
        {
            need = PolicyApi.MaintainFoodStorage;
        }
        else if (purpose == PolicyApi.CookAheadFood)
        {
            need = PolicyApi.MaintainRefrigeration;
        }
        _reviewer = reviewer;
        _purpose = purpose;
        _need = need;
    }

    internal (ConcernId Key, ConcernId Need) ReportTarget()
    {
        if (_purpose == PolicyApi.CookFood)
        {
            return (CookingBillsKey, _need);
        }
        if (_purpose == PolicyApi.PreserveFood)
        {
            return (PreservationBillsKey, _need);
        }
        if (_purpose == PolicyApi.CookAheadFood)
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
                    if (progress.Action.Kind == ActionKind.ProductionBill && DomainApi.StandardWorkOpen([progress]))
                    {
                        if (progress.Action.ProductionBill is { } b && _purpose == PolicyApi.ButcherFood && b.Mode == ButcherMode.Forever)
                        {
                            continue;
                        }
                        return true;
                    }
                }
                continue;
            }
            if (_purpose != PolicyApi.CookFood && StorePackage.PlanOpen(plan))
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
                report.Order = AwaitingFoodPlan(order.Subject);
                return report;
            case FoodOrderGap.Field:
                report.Order = FieldUnavailable(order.Subject);
                return report;
            case FoodOrderGap.NoDeficit:
                report.Order = BuildingReasonNoDeficit;
                return report;
            case FoodOrderGap.NoBill:
                report.Order = NoBill(projection.ProductionBenches, projection.Facts.Colonists);
                break;
            default:
                report.Order = WaitFor(PolicyApi.CauseMethodUsed, "ledger_bill");
                break;
        }
        if (order.ReserveRunning)
        {
            report.WorkTicks = ReserveBillWorkTicks;
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
        if (_purpose == PolicyApi.CookFood)
        {
            request.Meals = projection.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays);
        }
        else if (_purpose == PolicyApi.CookAheadFood)
        {
            var refrigeration = PolicyApi.ReviewRefrigeration(projection.Facts.FoodStorageUpkeep, review.Latches.Refrigeration, _reviewer.Policy.FoodStorage);
            request.Warm = refrigeration.WarmNutrition;
        }
        return request;
    }

    public Declared DeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        DeclareOrdersAll(snapshot, projection, benches, ct).For(_need);

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
            return AbstainOnRead(ct, PolicyApi.CauseUnreadReview);
        }
        FoodOrderRequest request;
        try
        {
            request = FoodRequest(projection, review);
        }
        catch (DomainException)
        {
            Note(new PurposeReport { Order = FieldUnavailable("food_facts") });
            return AbstainOnRead(ct, PolicyApi.CauseUnreadFoodFacts);
        }
        Note(FoodReport(request, projection));
        return PolicyApi.DeclareFoodOrders(request, benches);
    }

    internal Verdict NoBill(Fact<IReadOnlyList<ProductionBench>> benches, Fact<long> colonists)
    {
        if (!benches.TryGetValue(out var rows))
        {
            return FieldUnavailable("production_benches");
        }
        if (!colonists.IsKnown)
        {
            return FieldUnavailable("colonists");
        }
        var butcher = _purpose == PolicyApi.ButcherFood;
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
        BillGap.InProduction => WaitFor(PolicyApi.CauseExistingWork, subject),
        BillGap.NoRecipe => AwaitingPlan(subject, Wire.Name(gap)),
        BillGap.BenchFull => AwaitingPlan(subject, Wire.Name(gap)),
        BillGap.Waste => AwaitingPlan("mech_waste", ""),
        BillGap.NoCharger => AwaitingPlan("mech_charger", ""),
        _ => throw new InvalidOperationException($"bill gap \"{Wire.Name(gap)}\" is not one of the closed set"),
    };
}
