using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the food stores family (#3251, #3280): its full read (the rounds census, or with
/// refrigeration the room census and its cooling rooms, then the reserve stacks to hold or release while reserve
/// access is pending), the stock-target meal order that stores food, which the work ledger places under the
/// food-stores/storage Need, and the refrigeration Need's building work step.
/// </summary>
public sealed partial class FoodStoresRounds
{
    private readonly Rounder _reviewer;
    private readonly FacadeNativeClient _native;
    private readonly bool _storage;
    private readonly RoundsBuildingPlanner? _cooler;

    /// <summary>
    /// <paramref name="storage"/> derives the food-stores/storage Need; a <paramref name="cooler"/> planner
    /// (NewRoundsRefrigerationPlanner) derives food-stores/refrigeration and does its building work.
    /// </summary>
    public FoodStoresRounds(Rounder reviewer, FacadeNativeClient native, bool storage = true, RoundsBuildingPlanner? cooler = null)
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
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("food stores read: the scope is another world or older than the review");
        }
        RoundsReading reading;
        Fact<EquatableArray<string>> warm = default;
        Fact<bool> closet = default;
        if (_cooler is null)
        {
            reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, ct).ConfigureAwait(false);
        }
        else
        {
            // Refrigeration reads the room census: its warm stock stands in rooms and its cooling rooms are planned.
            reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, _cooler.RefrigerationDefinitions(), ct).ConfigureAwait(false);
            warm = RoundsBurialFacts.WarmCoolingRooms(reading.Projection);
            closet = RoundsBurialFacts.MealClosetOwed(reading.Projection);
        }
        var facts = FoodStoresFacts.From(reading.Projection.Facts) with
        {
            WarmRooms = warm,
            MealClosetOwed = closet,
        };
        if (!facts.StockProtection || facts.Reserve is not FoodReserveState.Reviewed { Review: var reserve } || !FoodStoresRules.ReserveAccessPending(facts.Reserve)
            || _native is not FacadeNativeClient supplies)
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
        (await DeclareMealOrderAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(FoodStoresRules.FoodStorageNeed.FilingKey.Value);

    /// <summary>
    /// While reserve access or a larder change is due only the standing stock orders stand; otherwise, with
    /// storage short of its target and every covered site full, one meal bill. An unread fact abstains.
    /// </summary>
    internal async Task<Declared> DeclareMealOrderAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
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
            case FoodStorageMethodKind.SiteUnread:
                return WorkRules.Abstaining(Cause.UnreadFoodStorage);
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
    private async Task<FoodStorageMethod> StorageChoiceAsync(C.Identity identity, FoodStorageObservation facts, FoodStorageReview review, CancellationToken ct)
    {
        var names = FoodStorageDefNames(facts, _reviewer.Policy.FoodStorage);
        var sites = new List<FoodStorageSite>(names.Count);
        var unread = false;
        foreach (var name in names)
        {
            ResourceStorage storage;
            try
            {
                (_, storage) = await _native.ReadResourceSourcesAsync(identity, name, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                unread = true;
                continue;
            }

            var spare = Math.Max(storage.Capacity - storage.Stored, 0);
            sites.Add(new FoodStorageSite { Id = name, Capacity = spare });
        }

        return FoodStorageUpkeepRules.SelectFoodStorageMethod(new FoodStoragePlanningRequest { Review = review, Sites = EquatableArray.From(sites), SiteUnread = unread, Resource = FoodStorageResourceDefinition });
    }
}

partial class FoodStoresRounds
{
    internal static readonly Resource FoodStorageResourceDefinition = new("MealSimple");

    internal const int FoodStorageSiteLimit = 16;

    internal static IReadOnlyList<string> FoodStorageDefNames(FoodStorageObservation observed, FoodStoragePolicy p)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in observed.Stocks)
        {
            if (!FoodStorageUpkeepRules.FoodStorageUnstored(entry, observed.ChilledMaxC, p))
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
