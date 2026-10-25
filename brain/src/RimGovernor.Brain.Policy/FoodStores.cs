using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Reserve targets are not requested until the reserve needs stack access. The full read supplies a measured list.
/// The food stores family (#3251, #3280) reads: the perishable food census, the travel reserve review, whether
/// a food stockpile stands, whether the food plan holds the stock-protection channel, the reserve stacks the native
/// supply reads offer to hold or release, and the
/// planned cooling rooms refrigeration answers for.
/// </summary>
public abstract record ReserveSupplyRead
{
    private protected ReserveSupplyRead() { }
    public sealed record NotRequested : ReserveSupplyRead;
    public sealed record Targets(EquatableArray<SupplyAllow> Values) : ReserveSupplyRead;
}

public sealed record FoodStoresFacts
{
    public FoodStorageObservation Storage { get; init; } = new();

    public FoodReserveState Reserve { get; init; } = new FoodReserveState.Disabled();

    /// <summary>Whether an owned zone of a food role stands (<see cref="StockpileRules.FoodStorageStanding"/>).</summary>
    public bool Stockpile { get; init; }

    /// <summary>Whether the food plan opened or holds the reserve's stock-protection channel; without it the family proposes nothing.</summary>
    public bool StockProtection { get; init; }

    /// <summary>The supply actions the native reserve reads offer, the release read first; not requested unless reserve access is pending.</summary>
    public ReserveSupplyRead ReserveTargets { get; init; } = new ReserveSupplyRead.NotRequested();

    /// <summary>The warm standing tombs, morgues and meal closets (<see cref="TombRules.WarmCoolingRooms"/>); empty while the plan or cooler research is unavailable.</summary>
    public EquatableArray<string> WarmRooms { get; init; }

    /// <summary>Whether the planned meal closet waits to be shelled while its dining room stands; false before the plan exists.</summary>
    public bool MealClosetOwed { get; init; }

    /// <summary>The facts a review already holds; the reserve targets have not been requested.</summary>
    public static FoodStoresFacts From(RoundsFacts f) => new()
    {
        Storage = f.FoodStorageUpkeep,
        Reserve = f.FoodReserve,
        Stockpile = StockpileRules.FoodStorageStanding(f),
        StockProtection = f.FoodPlan is { } plan && FoodOrdersRules.FoodPlanSupport(plan, CandidateKind.Reserve, "stock-protection"),
        WarmRooms = f.WarmRooms,
        MealClosetOwed = f.MealClosetOwed,
    };
}

/// <summary>
/// The food stores family (#3251, #3280): up to two colony-wide Needs, each derived only when its service is
/// composed, with no ordering between them. <c>food-stores/storage</c> is met while the food stockpile stands, the
/// travel reserve is held and filled, and at least <see cref="FoodStoragePolicy.TargetStoredFraction"/> of the
/// at-risk perishable nutrition sits in adequate storage; its Methods hold or release reserve stacks and allow or
/// forbid one larder corpse, while the reserve refill is the PreserveFood bill the work ledger places and the
/// stockpile is the stockpile planner's. <c>food-stores/refrigeration</c> is met while no at-risk nutrition sits warm
/// and no planned cooling room is owed; its coolers, setpoints and cold rooms are building work the runtime's work
/// step commits, and its solar-flare answer is the CookAheadFood bill the work ledger places.
/// </summary>
public sealed class FoodStoresFamily(FoodStoragePolicy policy, bool storage, bool refrigeration) : IFamily<FoodStoresFacts>
{
    /// <summary>The most supply actions one reserve Method carries.</summary>
    internal const int ReserveActionLimit = 8;

    public string Id => FoodStoresRules.FoodStoresFamilyId;

    /// <summary>The colony census; refrigeration also reads the room census its warm stock and cooling rooms stand in.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = refrigeration ? [FactFamily.Rooms, FactFamily.Colony] : [FactFamily.Colony];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(FoodStoresFacts facts) =>
    [
        .. storage ? [FoodStoresRules.FoodStorageNeedOf(facts, policy)] : Array.Empty<Need>(),
        .. refrigeration ? [FoodStoresRules.RefrigerationNeedOf(facts, policy)] : Array.Empty<Need>(),
    ];

    public IReadOnlyList<Need> DeriveSeriousHarm(FoodStoresFacts facts) => [];

    /// <summary>
    /// Pending reserve access first (one Method of at most eight supply actions), otherwise the larder's one corpse
    /// change. A corpse haul, the refill and the stockpile have no Method here.
    /// </summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, FoodStoresFacts facts)
    {
        if (!ready.Any(n => n.Id == FoodStoresRules.FoodStorageNeed) || !facts.StockProtection)
        {
            return [];
        }
        if (FoodStoresRules.ReserveAccessPending(facts.Reserve))
        {
            var supplies = ReserveSupplies(facts);
            return supplies.Count == 0
                ? []
                : [new NeedMethod(FoodStoresRules.FoodStorageNeed, new MethodId("food-reserve"), "reserve food to hold or release", id => Plan(id, supplies))];
        }
        CorpseLarderMethod larder;
        try
        {
            larder = CorpseLarderRules.SelectCorpseLarder(facts.Storage);
        }
        catch (DomainException)
        {
            return [];
        }
        if (larder is not { Kind: "allow" or "forbid", Stock: { } stock, Handling: { } handling })
        {
            return [];
        }
        var supply = larder.Kind == "allow"
            ? new SupplyAllow(stock.Id, stock.DefName, handling.Cell)
            : SupplyAllow.NewSupplyForbid(stock.Id, stock.DefName, handling.Cell);
        return [new NeedMethod(FoodStoresRules.FoodStorageNeed, new MethodId($"corpse-larder-{larder.Kind}-{stock.Id}"), $"larder corpse to {larder.Kind}", id => Plan(id, [supply]))];
    }

    /// <summary>The offered reserve stacks the review wants held (forbid) or released (allow), in read order, at most eight.</summary>
    private static List<SupplyAllow> ReserveSupplies(FoodStoresFacts facts)
    {
        if (facts.Reserve is not FoodReserveState.Reviewed { Review: var reserve })
        {
            return [];
        }
        if (facts.ReserveTargets is not ReserveSupplyRead.Targets read)
        {
            throw new RequiredFactException("reserve supply targets");
        }
        var targets = read.Values;
        var wanted = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in reserve.Hold)
        {
            wanted[id] = true;
        }
        foreach (var id in reserve.Release)
        {
            wanted[id] = false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. targets.Where(t => wanted.TryGetValue(t.Thing, out var hold) && hold == t.Forbidden && seen.Add(t.Thing)).Take(ReserveActionLimit)];
    }

    private static PlanSpec Plan(PlanId id, IReadOnlyList<SupplyAllow> supplies) =>
        new(id, new PlanRevision(1), supplies.Select((s, i) => DomainAction.NewSupplyAllowAction(new ActionId($"{id.Value}-{i}"), s)));
}

public static class FoodStoresRules
{
    public const string FoodStoresFamilyId = "food-stores";

    /// <summary>The food stores family's storage Need (#3251).</summary>
    public static readonly NeedId FoodStorageNeed = NeedId.Of(FoodStoresFamilyId, "storage");

    /// <summary>The food stores family's refrigeration Need (#3280).</summary>
    public static readonly NeedId RefrigerationNeed = NeedId.Of(FoodStoresFamilyId, "refrigeration");

    /// <summary>Whether the reserve review asks for stacks to be held or released.</summary>
    public static bool ReserveAccessPending(FoodReserveState reserve) =>
        reserve is FoodReserveState.Reviewed { Review: var r } && (r.Hold.Count > 0 || r.Release.Count > 0);

    /// <summary>
    /// The storage Need, a plain threshold with no latch: unmet while reserve access is pending, the reserve (outside
    /// an emergency) is short, a food stockpile is known missing, a larder corpse change is due, or the stored share
    /// of at-risk perishable nutrition is under <see cref="FoodStoragePolicy.TargetStoredFraction"/>, and else it is
    /// met; malformed census rows throw. Its deficit is the
    /// owed share, 0 to 1: pending access, a missing stockpile or a larder change are whole; a short reserve or
    /// storage is its shortfall over its target.
    /// </summary>
    public static Need FoodStorageNeedOf(FoodStoresFacts f, FoodStoragePolicy p)
    {
        var reserve = (f.Reserve as FoodReserveState.Reviewed)?.Review;
        var access = ReserveAccessPending(f.Reserve);
        var refill = reserve is { Emergency: false, DeficitNutrition: > 0 };
        var stockpileMissing = !f.Stockpile;
        FoodStorageReview storage;
        try
        {
            storage = FoodStorageUpkeepRules.ReviewFoodStorage(f.Storage, p);
        }
        catch (DomainException)
        {
            // Malformed census rows are an unreadable census, never a measured one.
            throw new RequiredFactException("food storage census");
        }
        var owed = new List<double>();
        if (access || stockpileMissing)
        {
            owed.Add(1.0);
        }
        if (refill)
        {
            owed.Add(reserve!.TargetNutrition > 0 ? Math.Min(1.0, reserve.DeficitNutrition / reserve.TargetNutrition) : 1.0);
        }
        if (storage.Active)
        {
            owed.Add(storage.Larder ? 1.0 : storage.Target > 0 ? Math.Min(1.0, storage.Deficit / storage.Target) : 1.0);
        }
        return new Need
        {
            Id = FoodStorageNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = owed.Count > 0 ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = owed.Count == 0 ? 0.0 : owed.Max(),
        };
    }

    /// <summary>
    /// The refrigeration Need, a plain threshold with no latch (decision 16): unmet while the at-risk roofed
    /// perishable nutrition warmer than <see cref="FoodStoragePolicy.ChilledExitC"/> reaches
    /// <see cref="FoodStoragePolicy.AtRiskNutritionThreshold"/>, while a standing tomb, morgue or meal closet measures
    /// warm, or while the planned meal closet waits for its shell. Otherwise it is met; malformed census rows throw.
    /// The cooling room and closet observations are total. Its deficit is the warm nutrition's share of the
    /// threshold, whole for a warm cooling room, and at least a half while the closet is owed.
    /// </summary>
    public static Need RefrigerationNeedOf(FoodStoresFacts f, FoodStoragePolicy p)
    {
        RefrigerationReview review;
        try
        {
            review = RefrigerationRules.ReviewRefrigeration(f.Storage, p).WithWarmRooms(f.WarmRooms);
        }
        catch (DomainException)
        {
            // Malformed census rows are an unreadable census, never a measured one.
            throw new RequiredFactException("refrigeration census");
        }
        var closet = f.MealClosetOwed;
        var unmet = review.Active || closet;
        var deficit = 0.0;
        if (unmet)
        {
            deficit = p.AtRiskNutritionThreshold > 0 ? Math.Min(1.0, review.WarmNutrition / p.AtRiskNutritionThreshold) : 1.0;
            if (review.Warm.Count > 0)
            {
                deficit = 1.0;
            }
            if (closet)
            {
                deficit = Math.Max(deficit, 0.5);
            }
        }
        return new Need
        {
            Id = RefrigerationNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = unmet ? Satisfaction.Unmet : Satisfaction.Met,
            Deficit = deficit,
        };
    }

}
