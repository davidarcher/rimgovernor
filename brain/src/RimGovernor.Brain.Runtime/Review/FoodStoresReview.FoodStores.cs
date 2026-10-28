using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The runtime half of the food stores family (#3251, #3280): its full read (the rounds census, or with
/// refrigeration the room census and its cooling rooms, then the reserve stacks to hold or release while reserve
/// access is pending), the catalog-selected preservation order, which the work ledger places under the
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
            reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, BuildingPlanner.RefrigerationDefinitions(), ct).ConfigureAwait(false);
            warm = BurialFacts.WarmCoolingRooms(reading.Reading);
            closet = BurialFacts.MealClosetOwed(reading.Reading);
        }
        var facts = FoodStoresFacts.From(reading.Reading.Facts) with
        {
            WarmRooms = warm,
            MealClosetOwed = closet,
        };
        if (facts.Reserve is not FoodReserveState.Reviewed { Review: var reserve })
        {
            return facts;
        }
        var directions = FoodStoresRules.ReserveDirections(reserve, facts.StockProtection);
        if (directions.Count == 0) return facts;
        var targets = new List<SupplyAllow>();
        foreach (var forbid in directions.Values.Distinct().Order())
        {
            var read = await _native.ReadFoodReserveSuppliesAsync(Boundary.Identity(scope.World), forbid, ct).ConfigureAwait(false);
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

    /// <summary>The storage Need's native preservation order, placed through the shared work ledger.</summary>
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclarePreservationOrdersAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(FoodStoresRules.FoodStorageNeed.FilingKey.Value);

    internal async Task<Declared> DeclarePreservationOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var f = projection.Facts;
        var plan = f.RequireFoodPlan();
        if (!FoodOrdersRules.FoodPlanSupport(plan, CandidateKind.Reserve, "stock-protection"))
            return WorkRules.Abstaining(Cause.UnreadFoodPlan);
        var production = f.EssentialInputs.Benches;
        if (FoodStoresRules.ReserveAccessPending(f.FoodReserve) || CorpseLarderRules.SelectCorpseLarder(f.FoodStorageUpkeep).Kind.Length != 0)
            return FoodOrdersRules.StandingStorageOrders(production, benches);
        var storage = FoodStorageUpkeepRules.ReviewFoodStorage(f.FoodStorageUpkeep, _reviewer.Policy.FoodStorage);
        if (!storage.Active) return new Declared();
        var choice = await StorageChoiceAsync(Boundary.Identity(snapshot), f.FoodStorageUpkeep, storage, ct).ConfigureAwait(false);
        if (choice != FoodStorageMethodKind.Produce) return new Declared();
        return FoodOrdersRules.FoodStorageOrders(storage.Deficit, production, benches, f.CombinedFoodSupply,
            ColonyPlanningRules.MealCooks(f.ForecastObserved.WorkPawns.Pawns),
            HumanFoodPlanRules.HumanCookingIngredients(f.CombinedFoodSupply, f.FoodSupply.Consumers, plan, HumanMeatRoute.Meals));
    }

    /// <summary>Read spare storage for each at-risk food definition; failed required reads stop the pass.</summary>
    private async Task<FoodStorageMethodKind> StorageChoiceAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, FoodStorageObservation facts, FoodStorageReview review, CancellationToken ct)
    {
        var names = FoodStorageUpkeepRules.FoodStorageDefNames(facts, _reviewer.Policy.FoodStorage);
        var sites = new List<FoodStorageSite>(names.Count);
        foreach (var name in names)
        {
            var (_, storage) = await _native.ReadResourceSourcesAsync(identity, name, ct).ConfigureAwait(false);
            sites.Add(new FoodStorageSite { Id = name, Capacity = FoodStorageUpkeepRules.StorageSpare(storage.Capacity, storage.Stored) });
        }
        return FoodStorageUpkeepRules.SelectFoodStorageMethod(review, sites);
    }
}
