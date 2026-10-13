namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsFoodStorageUpkeepSource
{
    (Observations.ColonyFactsReply Reply, BridgeResult Result) ReadColonyFacts(CancellationToken ct, Common.Identity identity, bool full);

    (IReadOnlyList<ResourceSourceRow> Rows, ResourceStorage Storage, BridgeResult Result) ReadResourceSources(CancellationToken ct, Common.Identity identity, string definition);

    (IReadOnlyList<GearBenchRead> Benches, BridgeResult Result) ReadGearBenches(CancellationToken ct, Common.Identity identity);

    Tables FrameTables(CancellationToken ct, Common.Identity identity);
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

public readonly record struct RoundsFoodStorageUpkeepResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    internal static readonly Resource FoodStorageResourceDefinition = new("MealSimple");

    internal const int FoodStorageSiteLimit = 16;

    internal static FoodStorageObservation FoodStorageObservationFacts(CancellationToken ct, IRoundsFoodStorageUpkeepSource native, Observations.ColonyFactsSnapshot v)
    {
        var food = v.FoodSupply?.Observed;
        if (food is null)
        {
            return new FoodStorageObservation();
        }

        try
        {
            var tables = native.FrameTables(ct, v.Context?.Identity!);
            var (supply, known) = ObservationPackage.DecodeFoodSupply(food, tables.Things, tables.Catalog);
            if (!known)
            {
                return new FoodStorageObservation();
            }

            return PolicyFunctions.FoodStorageStocks(supply, tables.Catalog.Derived.FullRotRateC);
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

    internal static Verdict FoodNotStoredVerdict(FoodStuckReason reason) => AwaitingPlan("food_not_stored", reason.WireName());

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
    internal RoundsFoodStorageUpkeepResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
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

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainFoodStorage);
        if (!workable)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(call, method.Plan);
            var pending = plan.Progress.Where(progress => progress.Action.Kind != ActionKind.ProductionBill).ToList();
            if (Standard.StandardWorkOpen(pending))
            {
                return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var identity = BoundaryPackage.Identity(state.Snapshot);
        var (reply, _) = Reviewer.ColonyFacts(call, Native, identity, false);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("step: no observed colony facts");
        }

        try
        {
            BridgePackage.ValidateColonyFacts(observed, identity);
        }
        catch (DomainException e)
        {
            throw new ControlException("step: colony facts invalid", e);
        }

        try
        {
            BoundaryPackage.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: observed context", e);
        }

        if (observed.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("step: observed facts older than the review");
        }

        var facts = FoodStorageObservationFacts(call, Native, observed);
        var expected = new ObservationIdentity
        {
            Colony = state.Snapshot.Colony,
            Load = state.Snapshot.Load,
            Map = state.Snapshot.Map,
            Tick = new Tick(observed.Context.Tick),
            NativeGeneration = Known(state.Snapshot.Native),
        };
        var reading = Reviewer.ObserveOwned(call, Reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
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

            return AdmitReserve(call, epoch, goal, observed, reserve);
        }

        var larder = PolicyFunctions.SelectCorpseLarder(facts);
        if (larder.Kind.Length != 0)
        {
            return AdmitCorpseLarder(call, epoch, arbiter, goal, observed, larder);
        }

        var foodReview = PolicyFunctions.ReviewFoodStorage(facts, review.Latches.FoodStorage, Reviewer.Policy.FoodStorage);
        if (!foodReview.Active)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = BuildingReasonNoDeficit };
        }

        var choice = StorageChoice(call, identity, facts, foodReview);
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

        var benches = GearBenches(call, identity);
        var (_, kind, ok) = PolicyFunctions.FoodStorageMealOrder(choice, benches);
        if (!ok)
        {
            return new RoundsFoodStorageUpkeepResult { Verdict = MedicineChoiceVerdict(kind, choice.Resource) };
        }

        return new RoundsFoodStorageUpkeepResult { Verdict = WaitFor(Cause.MethodUsed, "ledger_bill") };
    }

    internal IReadOnlyList<GearBench> GearBenches(CancellationToken ct, Common.Identity identity)
    {
        var (census, _) = Native.ReadGearBenches(ct, identity);
        return census.Select(row => row.Bench).ToList();
    }

    internal FoodStorageMethod StorageChoice(CancellationToken ct, Common.Identity identity, FoodStorageObservation facts, FoodStorageReview review)
    {
        var names = FoodStorageDefNames(facts, Reviewer.Policy.FoodStorage);
        var sites = new List<FoodStorageSite>(names.Count);
        foreach (var name in names)
        {
            ResourceStorage storage;
            try
            {
                (_, storage, _) = Native.ReadResourceSources(ct, identity, name);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                sites.Add(new FoodStorageSite { Id = name, Capacity = Unknown<long>() });
                continue;
            }

            var spare = Math.Max(storage.Capacity - storage.Stored, 0);
            sites.Add(new FoodStorageSite { Id = name, Capacity = Known(spare) });
        }

        return PolicyFunctions.SelectFoodStorageMethod(new FoodStoragePlanningRequest { Review = review, Sites = Known<IReadOnlyList<FoodStorageSite>>(sites), Resource = FoodStorageResourceDefinition });
    }

    public Declared DeclareOrders(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches)
    {
        return DeclareOrdersInner(ct, snapshot, projection, benches).For(ConcernId.MaintainFoodStorage);
    }

    internal Declared DeclareOrdersInner(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches)
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

        FoodStorageMethod larder;
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

        Review review;
        try
        {
            review = Reviewer.Player.Journal.LoadRounds(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(ct, Cause.UnreadReview, e);
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
            return default;
        }

        FoodStorageMethod choice;
        try
        {
            choice = StorageChoice(ct, BoundaryPackage.Identity(snapshot), f.FoodStorageUpkeep, foodReview);
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

        return default;
    }
}
