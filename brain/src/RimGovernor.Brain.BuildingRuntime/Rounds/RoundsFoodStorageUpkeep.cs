namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsFoodStorageUpkeepSource : Observation.IColonySource, Observation.IDefinitionSource, IColonyFactsReader
{
    Task<(IReadOnlyList<ResourceSource> Rows, ResourceStorage Storage, BridgeResult Result)> ReadResourceSourcesAsync(C.Identity identity, string definition, CancellationToken ct);

    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct);

}

/// <summary>Composes MaintainFoodStorage's rounds step: reserve holds, corpse larders and the meal order that stores food.</summary>
public sealed partial class RoundsFoodStorageUpkeepPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    internal IRoundsFoodStorageUpkeepSource Native { get; init; } = null!;

    public static RoundsFoodStorageUpkeepPlanner NewRoundsFoodStorageUpkeepPlanner(Rounder reviewer, IRoundsFoodStorageUpkeepSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsFoodStorageUpkeepPlanner: reviewer or native is missing");
        }

        return new RoundsFoodStorageUpkeepPlanner { Reviewer = reviewer, Native = native };
    }
}

public readonly record struct RoundsFoodStorageUpkeepResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    internal static readonly Resource FoodStorageResourceDefinition = new("MealSimple");

    internal const int FoodStorageSiteLimit = 16;

    internal static async Task<FoodStorageObservation> FoodStorageObservationFactsAsync(IRoundsFoodStorageUpkeepSource native, O.ColonyFactsSnapshot v, CancellationToken ct)
    {
        var food = v.FoodSupply?.Observed;
        if (food is null)
        {
            return new FoodStorageObservation();
        }

        try
        {
            var tables = await native.FrameTablesAsync(v.Context?.Identity!, ct).ConfigureAwait(false);
            var catalog = await native.DefinitionCatalogAsync(v.Context?.Identity!, ct).ConfigureAwait(false);
            var supply = ObservationPackage.DecodeFoodSupply(food, tables.Things, catalog);
            if (supply is null || catalog is null)
            {
                return new FoodStorageObservation();
            }

            return PolicyFunctions.FoodStorageStocks(supply, catalog.Derived.FullRotRateC);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new FoodStorageObservation();
        }
    }

    internal static IReadOnlyList<string> FoodStorageDefNames(FoodStorageObservation observed, FoodStoragePolicy p)
    {
        if (!observed.Stocks.TryGetValue(out var stocks))
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in stocks)
        {
            if (!PolicyFunctions.FoodStorageUnstored(entry, observed.ChilledMaxC, p))
            {
                continue;
            }

            var name = entry.Stock.DefName.Value;
            if (name.Length != 0)
            {
                names.Add(name);
            }
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).Take(FoodStorageSiteLimit).ToList();
    }

    internal static Verdict FoodNotStoredVerdict(FoodStuckReason reason) => AwaitingPlan("food_not_stored", Wire.Name(reason));

    internal static Verdict FoodStorageChoiceVerdict(FoodStorageMethodKind kind)
    {
        switch (kind)
        {
            case FoodStorageMethodKind.Recovered:
                return BuildingReasonNoDeficit;
            case FoodStorageMethodKind.Unknown:
                return FieldUnavailable("food_storage_capacity");
            default:
                return WaitFor(Cause.MethodUsed, "food_storage_method");
        }
    }
}

public sealed partial class RoundsFoodStorageUpkeepPlanner
{
    internal async Task<RoundsFoodStorageUpkeepResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainFoodStorage, call);
        if (!workable || goal is null)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            var pending = plan.Progress.Where(progress => progress.Action.Kind != ActionKind.ProductionBill).ToList();
            if (Standard.WorkOpen(pending))
            {
                return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var identity = Boundary.Identity(state.Snapshot);
        var reply = await Reviewer.ColonyFactsAsync(Native, identity, false, call).ConfigureAwait(false);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("step: no observed colony facts");
        }

        try
        {
            BridgeApi.ValidateColonyFacts(observed, identity);
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: observed context", e);
        }

        if (observed.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("step: observed facts older than the review");
        }

        var facts = await FoodStorageObservationFactsAsync(Native, observed, call).ConfigureAwait(false);
        var expected = new Identity
        {
            Colony = state.Snapshot.Colony,
            Load = state.Snapshot.Load,
            Map = state.Snapshot.Map,
            Tick = new Tick(observed.Context.Tick),
            NativeGeneration = Known(state.Snapshot.Native),
        };
        var reading = await Reviewer.ObserveOwnedAsync(Reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        if (!FoodPlanSupportOf(reading.Projection.Facts.FoodPlan, CandidateKind.Reserve, "stock-protection"))
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = AwaitingFoodPlan("stock-protection") };
        }

        if (reading.Projection.Facts.FoodReserve.TryGetValue(out var reserve) && (reserve.Hold.Count > 0 || reserve.Release.Count > 0))
        {
            if (reading.Projection.Identity.Tick != new Tick(observed.Context.Tick))
            {
                return new RoundsFoodStorageUpkeepResult { Verdict = FieldUnavailable("food_reserve") };
            }

            return await AdmitReserveAsync(goal, observed, reserve, call, epoch).ConfigureAwait(false);
        }

        var larder = PolicyFunctions.SelectCorpseLarder(facts);
        if (larder.Kind.Length != 0)
        {
            return AdmitCorpseLarder(arbiter, goal, observed, larder, call, epoch);
        }

        var foodReview = PolicyFunctions.ReviewFoodStorage(facts, review.Latches.FoodStorage, Reviewer.Policy.FoodStorage);
        if (!foodReview.Active)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonNoDeficit };
        }

        var choice = await StorageChoiceAsync(identity, facts, foodReview, call).ConfigureAwait(false);
        if (choice.Kind == FoodStorageMethodKind.Stuck)
        {
            return new RoundsFoodStorageUpkeepResult
            {
                Verdict = FoodNotStoredVerdict(PolicyFunctions.FoodStorageStuckReason(facts, Reviewer.Policy.FoodStorage, PolicyFunctions.DangerHaulers(reading.Projection.WorkPawns))),
            };
        }

        if (choice.Kind != FoodStorageMethodKind.Produce)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = FoodStorageChoiceVerdict(choice.Kind) };
        }

        var benches = await GearBenchesAsync(identity, call).ConfigureAwait(false);
        var (_, kind, ok) = PolicyFunctions.FoodStorageMealOrder(choice, benches);
        if (!ok)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = MedicineChoiceVerdict(kind, choice.Resource) };
        }

        return new RoundsFoodStorageUpkeepResult { Verdict = WaitFor(Cause.MethodUsed, "ledger_bill") };
    }

    internal async Task<IReadOnlyList<GearBench>> GearBenchesAsync(C.Identity identity, CancellationToken ct)
    {
        var (census, _) = await Native.ReadGearBenchesAsync(identity, ct).ConfigureAwait(false);
        return census.Select(row => row.Bench).ToList();
    }

    internal async Task<FoodStorageMethod> StorageChoiceAsync(C.Identity identity, FoodStorageObservation facts, FoodStorageReview review, CancellationToken ct)
    {
        var names = FoodStorageDefNames(facts, Reviewer.Policy.FoodStorage);
        var sites = new List<FoodStorageSite>(names.Count);
        foreach (var name in names)
        {
            ResourceStorage storage;
            try
            {
                (_, storage, _) = await Native.ReadResourceSourcesAsync(identity, name, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                sites.Add(new FoodStorageSite { Id = name, Capacity = Unknown<long>() });
                continue;
            }

            var spare = Math.Max(storage.Capacity - storage.Stored, 0);
            sites.Add(new FoodStorageSite { Id = name, Capacity = Known(spare) });
        }

        return PolicyFunctions.SelectFoodStorageMethod(new FoodStoragePlanningRequest { Review = review, Sites = Known(EquatableArray.From(sites)), Resource = FoodStorageResourceDefinition });
    }

    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        return (await DeclareOrdersInnerAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(PolicyFunctions.MaintainFoodStorage);
    }

    internal async Task<Declared> DeclareOrdersInnerAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var f = projection.Facts;
        if (!PolicyFunctions.FoodPlanSupport(f.FoodPlan, CandidateKind.Reserve, "stock-protection"))
        {
            return PolicyFunctions.Abstaining(Cause.UnreadFoodPlan);
        }

        var standing = PolicyFunctions.StandingStockOrders(benches, FoodStorageResourceDefinition);
        if (f.FoodReserve.TryGetValue(out var reserve) && (reserve.Hold.Count > 0 || reserve.Release.Count > 0))
        {
            return standing;
        }

        CorpseLarderMethod larder;
        try
        {
            larder = PolicyFunctions.SelectCorpseLarder(f.FoodStorageUpkeep);
        }
        catch (DomainException)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadFoodStorage);
        }

        if (larder.Kind.Length != 0)
        {
            return standing;
        }

        Rounds review;
        try
        {
            review = Reviewer.Player.Journal.LoadRounds(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(Cause.UnreadReview, e, ct);
        }

        FoodStorageReview foodReview;
        try
        {
            foodReview = PolicyFunctions.ReviewFoodStorage(f.FoodStorageUpkeep, review.Latches.FoodStorage, Reviewer.Policy.FoodStorage);
        }
        catch (DomainException)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadFoodStorage);
        }

        if (!foodReview.Active)
        {
            return new Declared();
        }

        FoodStorageMethod choice;
        try
        {
            choice = await StorageChoiceAsync(Boundary.Identity(snapshot), f.FoodStorageUpkeep, foodReview, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadFoodStorage);
        }

        switch (choice.Kind)
        {
            case FoodStorageMethodKind.Unknown:
                return PolicyFunctions.Abstaining(Cause.UnreadFoodStorage);
            case FoodStorageMethodKind.Produce:
                OrderSpec spec;
                MedicineMethodKind kind;
                bool ok;
                try
                {
                    (spec, kind, ok) = PolicyFunctions.FoodStorageMealOrder(choice, benches);
                }
                catch (DomainException)
                {
                    return PolicyFunctions.Abstaining(Cause.UnreadRecipes);
                }

                if (kind == MedicineMethodKind.Unknown)
                {
                    return PolicyFunctions.Abstaining(Cause.UnreadRecipes);
                }

                if (ok)
                {
                    return new Declared { Orders = [spec] };
                }

                break;
        }

        return new Declared();
    }
}
