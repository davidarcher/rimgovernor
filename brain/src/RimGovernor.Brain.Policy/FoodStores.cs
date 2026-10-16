using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the food stores family reads (#3251): the perishable food census, the travel reserve review, whether a
/// food stockpile stands, whether the food plan holds the stock-protection channel, and the reserve stacks the
/// native supply reads offer to hold or release (known only on a full family read; the pure uses leave it unknown).
/// </summary>
public sealed record FoodStoresFacts
{
    public FoodStorageObservation Storage { get; init; } = new();

    public Fact<FoodReserveReview> Reserve { get; init; }

    /// <summary>Whether an owned zone of a food role stands (<see cref="PolicyFunctions.FoodStorageStanding"/>); unknown until zone claims are read.</summary>
    public Fact<bool> Stockpile { get; init; }

    /// <summary>Whether the food plan opened or holds the reserve's stock-protection channel; without it the family proposes nothing.</summary>
    public bool StockProtection { get; init; }

    /// <summary>The supply actions the native reserve reads offer, the release read first; unknown when not read.</summary>
    public Fact<EquatableArray<SupplyAllow>> ReserveTargets { get; init; }

    /// <summary>The colonists who haul in danger (<see cref="PolicyFunctions.DangerHaulers"/>): explain names why unstored food stays put.</summary>
    public Fact<EquatableArray<PawnId>> Haulers { get; init; }

    /// <summary>The facts a review already holds; the reserve targets stay unknown.</summary>
    public static FoodStoresFacts From(RoundsFacts f) => new()
    {
        Storage = f.FoodStorageUpkeep,
        Reserve = f.FoodReserve,
        Stockpile = PolicyFunctions.FoodStorageStanding(f),
        StockProtection = PolicyFunctions.FoodPlanSupport(f.FoodPlan, CandidateKind.Reserve, "stock-protection"),
        Haulers = f.DangerHaulers,
    };
}

/// <summary>
/// The food stores family (#3251), storage only: one colony-wide Need, <c>food-stores/storage</c>, met while the
/// food stockpile stands, the travel reserve is held and filled, and at least <see cref="FoodStoragePolicy.TargetStoredFraction"/>
/// of the at-risk perishable nutrition sits in adequate storage. Its Methods hold or release reserve stacks and
/// allow or forbid one larder corpse; the reserve refill is the PreserveFood bill the work ledger places, and the
/// stockpile is the stockpile planner's, so those leave the Need unmet with no Method of this family.
/// </summary>
public sealed class FoodStoresFamily(FoodStoragePolicy policy) : IFamily<FoodStoresFacts>
{
    /// <summary>The most supply actions one reserve Method carries.</summary>
    internal const int ReserveActionLimit = 8;

    public string Id => PolicyFunctions.FoodStoresFamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony];

    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = [];

    public IReadOnlyList<Need> Derive(FoodStoresFacts facts) => [PolicyFunctions.FoodStorageNeedOf(facts, policy)];

    /// <summary>
    /// Pending reserve access first (one Method of at most eight supply actions), otherwise the larder's one corpse
    /// change. A corpse haul, the refill and the stockpile have no Method here.
    /// </summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, FoodStoresFacts facts)
    {
        if (!ready.Any(n => n.Id == PolicyFunctions.FoodStorageNeed) || !facts.StockProtection)
        {
            return [];
        }
        if (PolicyFunctions.ReserveAccessPending(facts.Reserve))
        {
            var supplies = ReserveSupplies(facts);
            return supplies.Count == 0
                ? []
                : [new NeedMethod(PolicyFunctions.FoodStorageNeed, new MethodId("food-reserve"), "reserve food to hold or release", id => Plan(id, supplies))];
        }
        CorpseLarderMethod larder;
        try
        {
            larder = PolicyFunctions.SelectCorpseLarder(facts.Storage);
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
            ? new SupplyAllow(stock.Id, stock.DefName.Value, handling.Cell)
            : SupplyAllow.NewSupplyForbid(stock.Id, stock.DefName.Value, handling.Cell);
        return [new NeedMethod(PolicyFunctions.FoodStorageNeed, new MethodId($"corpse-larder-{larder.Kind}-{stock.Id}"), $"larder corpse to {larder.Kind}", id => Plan(id, [supply]))];
    }

    /// <summary>The offered reserve stacks the review wants held (forbid) or released (allow), in read order, at most eight.</summary>
    private static List<SupplyAllow> ReserveSupplies(FoodStoresFacts facts)
    {
        if (!facts.Reserve.TryGetValue(out var reserve) || !facts.ReserveTargets.TryGetValue(out var targets))
        {
            return [];
        }
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

public static partial class PolicyFunctions
{
    public const string FoodStoresFamilyId = "food-stores";

    /// <summary>The food stores family's storage Need (#3251).</summary>
    public static readonly NeedId FoodStorageNeed = NeedId.Of(FoodStoresFamilyId, "storage");

    /// <summary>Whether the reserve review asks for stacks to be held or released.</summary>
    public static bool ReserveAccessPending(Fact<FoodReserveReview> reserve) =>
        reserve.TryGetValue(out var r) && (r.Hold.Count > 0 || r.Release.Count > 0);

    /// <summary>
    /// The storage Need, a plain threshold with no latch: unmet while reserve access is pending, the reserve (outside
    /// an emergency) is short, a food stockpile is known missing, a larder corpse change is due, or the stored share
    /// of at-risk perishable nutrition is under <see cref="FoodStoragePolicy.TargetStoredFraction"/>. Otherwise it is
    /// unknown when the census is (an unread reserve or stockpile is not "missing"), and met. Its deficit is the
    /// owed share, 0 to 1: pending access, a missing stockpile or a larder change are whole; a short reserve or
    /// storage is its shortfall over its target.
    /// </summary>
    public static Need FoodStorageNeedOf(FoodStoresFacts f, FoodStoragePolicy p)
    {
        var reserveKnown = f.Reserve.TryGetValue(out var reserve);
        var access = ReserveAccessPending(f.Reserve);
        var refill = reserveKnown && !reserve!.Emergency && reserve.DeficitNutrition > 0;
        var stockpileMissing = Negative(f.Stockpile);
        FoodStorageReview? storage;
        try
        {
            storage = ReviewFoodStorage(f.Storage, p);
        }
        catch (DomainException)
        {
            // Malformed census rows are an unreadable census, never a measured one.
            storage = null;
        }
        var census = storage is not null && storage.TotalNutrition.IsKnown;
        var owed = new List<double>();
        if (access || stockpileMissing)
        {
            owed.Add(1.0);
        }
        if (refill)
        {
            owed.Add(reserve!.TargetNutrition > 0 ? Math.Min(1.0, reserve.DeficitNutrition / reserve.TargetNutrition) : 1.0);
        }
        if (census && storage!.Active)
        {
            owed.Add(storage.Larder ? 1.0 : storage.Target.TryGetValue(out var target) && target > 0 && storage.Deficit.TryGetValue(out var deficit) ? Math.Min(1.0, deficit / target) : 1.0);
        }
        var satisfaction = owed.Count > 0 ? Satisfaction.Unmet : census ? Satisfaction.Met : Satisfaction.Unknown;
        return new Need
        {
            Id = FoodStorageNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = satisfaction == Satisfaction.Unknown ? Unknown<double>() : Known(owed.Count == 0 ? 0.0 : owed.Max()),
            Evidence =
            [
                new("reserve_access", reserveKnown ? Known($"hold:{reserve!.Hold.Count} release:{reserve.Release.Count}") : Unknown<string>()),
                new("reserve_deficit_nutrition", reserveKnown ? Known(reserve!.Emergency ? "emergency" : reserve.DeficitNutrition.ToString("0.##", CultureInfo.InvariantCulture)) : Unknown<string>()),
                new("food_stockpile", f.Stockpile.TryGetValue(out var standing) ? Known(standing ? "standing" : "missing") : Unknown<string>()),
                new("stored_nutrition", Nutrition(storage?.StoredNutrition ?? default)),
                new("unstored_nutrition", Nutrition(storage?.UnstoredNutrition ?? default)),
                new("corpse_larder", census ? Known(storage!.Larder ? "change" : "none") : Unknown<string>()),
                new("food_not_stored", census && storage!.Deficit.TryGetValue(out var shortfall) && shortfall > 0 ? Known(Wire.Name(FoodStorageStuckReason(f.Storage, p, f.Haulers))) : Unknown<string>()),
            ],
        };
    }

    private static Fact<string> Nutrition(Fact<double> v) =>
        v.TryGetValue(out var n) ? Known(n.ToString("0.##", CultureInfo.InvariantCulture)) : Unknown<string>();
}
