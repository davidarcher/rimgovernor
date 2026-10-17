namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the food stores family beside the rounds census: one resource's storage sites for the meal order.</summary>
public interface IRoundsFoodStoresSource
{
    Task<(IReadOnlyList<ResourceSource> Rows, ResourceStorage Storage)> ReadResourceSourcesAsync(C.Identity identity, string definition, CancellationToken ct);
}

/// <summary>
/// The runtime half of the food stores family (#3251, #3280): its full read (the rounds census, or with
/// refrigeration the room census and its cooling rooms, then the reserve stacks to hold or release while reserve
/// access is pending), the stock-target meal order that stores food, which the work ledger places under the
/// food-stores/storage Need, and the refrigeration Need's building work step.
/// </summary>
public sealed class FoodStoresRounds
{
    private readonly Rounder _reviewer;
    private readonly IRoundsFoodStoresSource _native;
    private readonly bool _storage;
    private readonly RoundsBuildingPlanner? _cooler;

    /// <summary>
    /// <paramref name="storage"/> derives the food-stores/storage Need; a <paramref name="cooler"/> planner
    /// (NewRoundsRefrigerationPlanner) derives food-stores/refrigeration and does its building work.
    /// </summary>
    public FoodStoresRounds(Rounder reviewer, IRoundsFoodStoresSource native, bool storage = true, RoundsBuildingPlanner? cooler = null)
    {
        _reviewer = reviewer ?? throw new ControlException("FoodStoresRounds: reviewer is missing");
        _native = native ?? throw new ControlException("FoodStoresRounds: native is missing");
        if (!storage && cooler is null)
        {
            throw new ControlException("FoodStoresRounds: neither storage nor refrigeration is composed");
        }
        _storage = storage;
        _cooler = cooler;
    }

    /// <summary>
    /// The family's scheduler entry: it wakes on the colony facts and on its supply outcomes (a haul outcome too,
    /// though it proposes none), and with refrigeration on its cooler, cold-room and setpoint outcomes.
    /// </summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<FoodStoresFacts>(new FoodStoresFamily(_reviewer.Policy.FoodStorage, _storage, _cooler is not null), ReadAsync,
            new FamilySchedule
            {
                Kinds = _cooler is null
                    ? [ActionKind.Haul, ActionKind.SupplyAllow, ActionKind.SupplyForbid]
                    : [ActionKind.Haul, ActionKind.SupplyAllow, ActionKind.SupplyForbid, ActionKind.Building, ActionKind.BuildingTemperature],
            },
            WorkAsync);

    /// <summary>
    /// The building work of a ready Need: the refrigeration Need's cooler, setpoint and cold rooms. Storage has no
    /// building work; its Methods are proposed.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, FoodStoresFacts facts, CancellationToken call)
    {
        if (work.Need.Id != PolicyFunctions.RefrigerationNeed || _cooler is null)
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        return await _cooler.RefrigerationStepAsync(work, call).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world: the owned rounds census, and while the reserve
    /// asks for access and the food plan holds stock protection, the native reserve stacks. Throws when the read
    /// is from another world, older than the census, or slower than the review's age bound.
    /// </summary>
    internal async Task<FoodStoresFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        var started = _reviewer.Clock.Now();
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScopeAsync(_reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("food stores read: the scope is another world or older than the review");
        }
        RoundsReading reading;
        Fact<EquatableArray<string>> warm = default;
        Fact<bool> closet = default;
        if (_cooler is null)
        {
            reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ct).ConfigureAwait(false);
        }
        else
        {
            // Refrigeration reads the room census: its warm stock stands in rooms and its cooling rooms are planned.
            reading = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), _cooler.RefrigerationDefinitions(), ct).ConfigureAwait(false);
            warm = WarmCoolingRooms(reading.Projection);
            closet = MealClosetOwed(reading.Projection);
        }
        var facts = FoodStoresFacts.From(reading.Projection.Facts) with
        {
            Haulers = PolicyFunctions.DangerHaulers(reading.Projection.WorkPawns),
            WarmRooms = warm,
            MealClosetOwed = closet,
        };
        if (!facts.StockProtection || !facts.Reserve.TryGetValue(out var reserve) || !PolicyFunctions.ReserveAccessPending(facts.Reserve)
            || _native is not IFoodReserveSupplyReader supplies)
        {
            return facts;
        }

        var targets = new List<SupplyAllow>();
        foreach (var forbid in new[] { false, true })
        {
            if ((forbid ? reserve.Hold : reserve.Release).Count == 0)
            {
                continue;
            }
            var read = await supplies.ReadFoodReserveSuppliesAsync(Boundary.Identity(scope.World), forbid, ct).ConfigureAwait(false);
            try
            {
                Boundary.Context(read.Context, scope.World);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ControlException("food stores read: reserve supplies context", e);
            }
            if ((read.Context?.Tick ?? 0) < reading.Projection.Identity.Tick.Value)
            {
                throw new ControlException("food stores read: reserve supplies older than the census");
            }
            targets.AddRange(read.Targets.Select(t => t.Supply));
        }
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("food stores read: the read took too long");
        }
        return facts with { ReserveTargets = Known(EquatableArray.From(targets)) };
    }

    /// <summary>The stock-target meal order that stores food, filed under the food-stores/storage Need.</summary>
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareMealOrderAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(PolicyFunctions.FoodStorageNeed.FilingKey.Value);

    /// <summary>
    /// While reserve access or a larder change is due only the standing stock orders stand; otherwise, with
    /// storage short of its target and every covered site full, one meal bill. An unread fact abstains.
    /// </summary>
    internal async Task<Declared> DeclareMealOrderAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var f = projection.Facts;
        if (!PolicyFunctions.FoodPlanSupport(f.FoodPlan, CandidateKind.Reserve, "stock-protection"))
        {
            return PolicyFunctions.Abstaining(Cause.UnreadFoodPlan);
        }

        var standing = PolicyFunctions.StandingStockOrders(benches, FoodStorageResourceDefinition);
        if (PolicyFunctions.ReserveAccessPending(f.FoodReserve))
        {
            return standing;
        }

        FoodStorageReview storage;
        try
        {
            if (PolicyFunctions.SelectCorpseLarder(f.FoodStorageUpkeep).Kind.Length != 0)
            {
                return standing;
            }
            storage = PolicyFunctions.ReviewFoodStorage(f.FoodStorageUpkeep, _reviewer.Policy.FoodStorage);
        }
        catch (DomainException)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadFoodStorage);
        }

        if (!storage.Active)
        {
            return new Declared();
        }

        FoodStorageMethod choice;
        try
        {
            choice = await StorageChoiceAsync(Boundary.Identity(snapshot), f.FoodStorageUpkeep, storage, ct).ConfigureAwait(false);
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

    /// <summary>Reads the spare room of every covered site the unstored food could go to and picks stuck or produce.</summary>
    private async Task<FoodStorageMethod> StorageChoiceAsync(C.Identity identity, FoodStorageObservation facts, FoodStorageReview review, CancellationToken ct)
    {
        var names = FoodStorageDefNames(facts, _reviewer.Policy.FoodStorage);
        var sites = new List<FoodStorageSite>(names.Count);
        foreach (var name in names)
        {
            ResourceStorage storage;
            try
            {
                (_, storage) = await _native.ReadResourceSourcesAsync(identity, name, ct).ConfigureAwait(false);
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
}

internal static partial class BuildingRuntimePackage
{
    internal static readonly Resource FoodStorageResourceDefinition = new("MealSimple");

    internal const int FoodStorageSiteLimit = 16;

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
}
