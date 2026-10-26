using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The runtime half of the food stores family (#3251, #3280): its full read (the rounds census, or with
/// refrigeration the room census and its cooling rooms, then the reserve stacks to hold or release while reserve
/// access is pending), the stock-target meal order that stores food, which the work ledger places under the
/// food-stores/storage Need, and the refrigeration Need's building work step.
/// </summary>
public sealed partial class FoodStoresReview
{
    private readonly ColonyReview _reviewer;
    private readonly GameClient _native;
    private readonly bool _storage;
    private readonly BuildingPlanner? _cooler;

    /// <summary>
    /// <paramref name="storage"/> derives the food-stores/storage Need; a <paramref name="cooler"/> planner
    /// (NewReviewRefrigerationPlanner) derives food-stores/refrigeration and does its building work.
    /// </summary>
    public FoodStoresReview(ColonyReview reviewer, GameClient native, bool storage = true, BuildingPlanner? cooler = null)
    {
        _reviewer = reviewer ?? throw new ControlException("FoodStoresReview: reviewer is missing");
        _native = native ?? throw new ControlException("FoodStoresReview: native is missing");
        if (!storage && cooler is null)
        {
            throw new ControlException("FoodStoresReview: neither storage nor refrigeration is composed");
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
                // The food storage upkeep reads the stockpile zones.
                Sections = [FactSection.Zones],
            },
            WorkAsync);

    /// <summary>
    /// The building work of a ready Need: the refrigeration Need's cooler, setpoint and cold rooms. Storage has no
    /// building work; its Methods are proposed.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, FoodStoresFacts facts, CancellationToken call)
    {
        if (work.Need.Id != FoodStoresRules.RefrigerationNeed || _cooler is null)
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
        var review = await _reviewer.Player.Journal.LoadReviewAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("food stores read: the scope is another world or older than the review");
        }
        ReviewReading reading;
        EquatableArray<string> warm = [];
        var closet = false;
        if (_cooler is null)
        {
            reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, ct).ConfigureAwait(false);
        }
        else
        {
            // Refrigeration reads the room census: its warm stock stands in rooms and its cooling rooms are planned.
            reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, _cooler.RefrigerationDefinitions(), ct).ConfigureAwait(false);
            warm = BurialFacts.WarmCoolingRooms(reading.Reading);
            closet = BurialFacts.MealClosetOwed(reading.Reading);
        }
        var facts = FoodStoresFacts.From(reading.Reading.Facts) with
        {
            WarmRooms = warm,
            MealClosetOwed = closet,
        };
        if (!facts.StockProtection || facts.Reserve is not FoodReserveState.Reviewed { Review: var reserve } || !FoodStoresRules.ReserveAccessPending(facts.Reserve)
            || _native is not GameClient supplies)
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
            if ((read.Context?.Tick ?? 0) < reading.Reading.Identity.Tick.Value)
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
        return facts with { ReserveTargets = new ReserveSupplyRead.Targets(EquatableArray.From(targets)) };
    }

    /// <summary>The stock-target meal order that stores food, filed under the food-stores/storage Need.</summary>
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareMealOrderAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(FoodStoresRules.FoodStorageNeed.FilingKey.Value);

    /// <summary>
    /// While reserve access or a larder change is due only the standing stock orders stand; otherwise, with
    /// storage short of its target and every covered site full, one meal bill. An unread fact abstains.
    /// </summary>
    internal async Task<Declared> DeclareMealOrderAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var f = projection.Facts;
        if (f.FoodPlan is not { } storesPlan || !FoodOrdersRules.FoodPlanSupport(storesPlan, CandidateKind.Reserve, "stock-protection"))
        {
            return WorkRules.Abstaining(Cause.UnreadFoodPlan);
        }

        var standing = FoodOrdersRules.StandingStockOrders(benches, FoodStorageResourceDefinition);
        if (FoodStoresRules.ReserveAccessPending(f.FoodReserve))
        {
            return standing;
        }

        FoodStorageReview storage;
        try
        {
            if (CorpseLarderRules.SelectCorpseLarder(f.FoodStorageUpkeep).Kind.Length != 0)
            {
                return standing;
            }
            storage = FoodStorageUpkeepRules.ReviewFoodStorage(f.FoodStorageUpkeep, _reviewer.Policy.FoodStorage);
        }
        catch (DomainException)
        {
            return WorkRules.Abstaining(Cause.UnreadFoodStorage);
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
            return WorkRules.Abstaining(Cause.UnreadFoodStorage);
        }

        switch (choice.Kind)
        {
            case FoodStorageMethodKind.Produce:
                OrderSpec spec;
                MedicineMethodKind kind;
                bool ok;
                try
                {
                    (spec, kind, ok) = FoodOrdersRules.FoodStorageMealOrder(choice, benches);
                }
                catch (DomainException)
                {
                    return WorkRules.Abstaining(Cause.UnreadRecipes);
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
    private async Task<FoodStorageMethod> StorageChoiceAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, FoodStorageObservation facts, FoodStorageReview review, CancellationToken ct)
    {
        var names = FoodStorageUpkeepRules.FoodStorageDefNames(facts, _reviewer.Policy.FoodStorage);
        var sites = new List<FoodStorageSite>(names.Count);
        foreach (var name in names)
        {
            var (_, storage) = await _native.ReadResourceSourcesAsync(identity, name, ct).ConfigureAwait(false);

            var spare = FoodStorageUpkeepRules.StorageSpare(storage.Capacity, storage.Stored);
            sites.Add(new FoodStorageSite { Id = name, Capacity = spare });
        }

        return FoodStorageUpkeepRules.SelectFoodStorageMethod(new FoodStoragePlanningRequest { Review = review, Sites = EquatableArray.From(sites), Resource = FoodStorageResourceDefinition });
    }
}

partial class FoodStoresReview
{
    internal static readonly Resource FoodStorageResourceDefinition = new("MealSimple");

}
