namespace RimGovernor.Brain.Policy;

/// <summary>
/// Wraps an observed <see cref="FoodStock"/> (reused as-is from FoodForecast.cs). Whether the stock counts as
/// adequately stored is derived per review from its place, temperature and rot by <see cref="Stored"/>,
/// so the storage and refrigeration policies share one definition.
/// </summary>
public sealed record FoodStorageStock(FoodStock Stock)
{
    /// <summary>A stock a pawn carries is in no storage: the storage review and refrigeration leave it out.</summary>
    internal bool Carried => Stock.Place is FoodPlace.Carried;

    /// <summary>
    /// Whether a standing perishable stock sits in adequate storage: roofed, and either
    /// chilled to chilledMaxC or with at least SafeRotDays of runway left at its current temperature. A
    /// roofed-but-warm stockpile is adequate for food that will be eaten long before it rots; only warm food
    /// close to spoiling is at risk.
    /// </summary>
    internal bool Stored(double chilledMaxC, FoodStoragePolicy p) =>
        Stock.Place is FoodPlace.Stands { Roofed: true }
        && (Stock.TemperatureC <= chilledMaxC || Stock.Rot is FoodRot.Keeps || Stock.Rot is FoodRot.Rots { Ticks: var ticks } && ticks >= p.SafeRotDays * GameTime.TicksPerDay);
}

/// <summary>
/// The complete per-cycle census the food stores family's storage Need reviews: every observed stock, the barred
/// ones included.
/// </summary>
public sealed record FoodStorageObservation
{
    public EquatableArray<FoodStorageStock> Stocks { get; init; }

    /// <summary>The larder facts beside the census.</summary>
    public FoodLarder Larder { get; init; } = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] };

    /// <summary>
    /// The storage temperature at or below which perishable stock counts as refrigerated: the catalog's
    /// full_rot_rate_c, the temperature where the game's rot rate reaches its full rate.
    /// </summary>
    public double ChilledMaxC { get; init; }

    /// <summary>Rejects malformed or duplicate stock rows. Throws <see cref="DomainException"/>.</summary>
    public void Validate()
    {
        DomainException.Require(FoodStorageUpkeepRules.Celsius(ChilledMaxC), "invalid food storage facts");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Stocks)
        {
            var s = entry.Stock;
            DomainException.Require(FoodForecastRules.FoodId(s.Id) && seen.Add(s.Id), "invalid food storage facts");
            DomainException.Require(FoodForecastRules.FoodNumber(s.Nutrition), "invalid food storage facts");
            DomainException.Require(s.Rot is not FoodRot.Rots { Ticks: < 0 }, "invalid food storage facts");
            DomainException.Require(FoodStorageUpkeepRules.Celsius(s.TemperatureC), "invalid food storage facts");
        }
    }
}

/// <summary>
/// Names the fraction of at-risk perishable nutrition ReviewFoodStorage wants adequately stored (a plain
/// threshold, #3251), plus a floor below which a tiny at-risk amount never counts as a deficit (avoiding thrash
/// over a single dropped ration).
/// </summary>
public sealed record FoodStoragePolicy
{
    public double TargetStoredFraction { get; init; }

    public double AtRiskNutritionThreshold { get; init; }

    /// <summary>Our own tuning (the game defines no safe days): the rot runway that makes warm roofed stock acceptable anyway.</summary>
    public double SafeRotDays { get; init; }

    /// <summary>The storage temperature the food-stores/refrigeration Need is met at: at-risk stock warmer than it counts as warm (at or under FoodStorageObservation.ChilledMaxC).</summary>
    public double ChilledExitC { get; init; }

    /// <summary>The setpoint the food-stores/refrigeration Need asks a cooler for.</summary>
    public double FreezerTargetC { get; init; }

    internal bool Valid =>
        FoodForecastRules.FoodNumber(TargetStoredFraction) && FoodForecastRules.FoodNumber(AtRiskNutritionThreshold)
        && FoodForecastRules.FoodNumber(SafeRotDays) && FoodStorageUpkeepRules.Celsius(ChilledExitC) && FoodStorageUpkeepRules.Celsius(FreezerTargetC)
        && FreezerTargetC <= ChilledExitC
        && TargetStoredFraction > 0 && TargetStoredFraction <= 1;
}

/// <summary>
/// The storage review over what stands now, with no latch (#3251): Active while the stored share is under
/// TargetStoredFraction or a larder corpse change is due.
/// </summary>
public sealed record FoodStorageReview
{
    public bool Active { get; init; }

    /// <summary>Whether the larder has a corpse change due (part of Active).</summary>
    public bool Larder { get; init; }

    /// <summary>The nutrition that should sit in adequate storage.</summary>
    public double Target { get; init; }

    /// <summary>The nutrition short of Target, 0 while the review is inactive.</summary>
    public double Deficit { get; init; }
}

public enum FoodStorageMethodKind
{
    [WireName("recovered")]
    Recovered,

    /// <summary>A covered site already has room, so the stock is unstored despite available capacity.</summary>
    [WireName("stuck")]
    Stuck,

    [WireName("produce")]
    Produce,

}

/// <summary>
/// Observed spare item capacity in covered/enclosed storage accepting an at-risk food definition.
/// Vanilla hauling owns relocation; this is neither a nutrition measurement nor a reservation.
/// </summary>
public sealed record FoodStorageSite
{
    public string Id { get; init; } = "";

    public long Capacity { get; init; }
}

public static class FoodStorageUpkeepRules
{
    public const int FoodStorageSiteLimit = 16;

    public static IReadOnlyList<string> FoodStorageDefNames(FoodStorageObservation observed, FoodStoragePolicy p)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in observed.Stocks)
        {
            if (!FoodStorageUpkeepRules.FoodStorageUnstored(entry, observed.ChilledMaxC, p))
            {
                continue;
            }

            var name = entry.Stock.DefName;
            if (name.Length != 0)
            {
                names.Add(name);
            }
        }

        return names.OrderBy(n => n, StringComparer.Ordinal).Take(FoodStorageSiteLimit).ToList();
    }

    public static long StorageSpare(long capacity, long stored) => Math.Max(capacity - stored, 0);

    /// <summary>Lifts a food-supply census into the storage-upkeep census. chilledMaxC is the catalog's full-rot-rate temperature.</summary>
    public static FoodStorageObservation FoodStorageStocks(FoodSupply supply, double chilledMaxC) =>
        new()
        {
            Stocks = EquatableArray.From(supply.Stocks.Concat(supply.Barred).Select(stock => new FoodStorageStock(stock))),
            Larder = supply.Larder,
            ChilledMaxC = chilledMaxC,
        };

    /// <summary>Accepts any finite temperature, below zero included.</summary>
    internal static bool Celsius(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

    /// <summary>Whether a standing stock is perishable, not yet rotted and not adequately stored under p and the census's chilledMaxC.</summary>
    public static bool FoodStorageUnstored(FoodStorageStock s, double chilledMaxC, FoodStoragePolicy p)
    {
        if (s.Stock.Rot is not FoodRot.Rots { Ticks: > 0 })
        {
            return false;
        }
        return !s.Carried && !s.Stored(chilledMaxC, p);
    }

    public static FoodStoragePolicy DefaultFoodStoragePolicy() =>
        new()
        {
            TargetStoredFraction = 0.9,
            AtRiskNutritionThreshold = 5,
            SafeRotDays = 5,
            ChilledExitC = 5,
            FreezerTargetC = -5,
        };

    /// <summary>
    /// Tallies perishable, not-yet-rotted nutrition across every observed stock,
    /// splitting it into stored and unstored piles. It is active while the unstored pile clears
    /// AtRiskNutritionThreshold and the stored share of total perishable nutrition is under TargetStoredFraction,
    /// with no latch (#3251). Non-perishable and already-rotted stock never contributes: it is not at risk of being
    /// lost to inadequate storage. Throws <see cref="DomainException"/> for malformed rows.
    /// </summary>
    public static FoodStorageReview ReviewFoodStorage(FoodStorageObservation v, FoodStoragePolicy p)
    {
        var r = new FoodStorageReview();
        DomainException.Require(p.Valid, "invalid food storage facts or thresholds");
        try
        {
            v.Validate();
        }
        catch (DomainException)
        {
            throw new DomainException("invalid food storage facts or thresholds");
        }
        double stored = 0;
        double unstored = 0;
        double total = 0;
        foreach (var entry in v.Stocks)
        {
            var s = entry.Stock;
            var nutrition = s.Nutrition;
            if (entry.Carried || s.Rot is not FoodRot.Rots { Ticks: > 0 })
            {
                continue;
            }
            var storedFact = entry.Stored(v.ChilledMaxC, p);
            DomainException.Require(double.IsFinite(total + nutrition), "invalid food storage facts or thresholds");
            total += nutrition;
            if (storedFact)
            {
                stored += nutrition;
            }
            else
            {
                unstored += nutrition;
            }
        }
        DomainException.Require(FoodForecastRules.FoodNumber(stored) && FoodForecastRules.FoodNumber(unstored) && FoodForecastRules.FoodNumber(total), "invalid food storage facts or thresholds");
        if (total == 0)
        {
            return r;
        }
        var threshold = p.TargetStoredFraction * total;
        var isActive = unstored >= p.AtRiskNutritionThreshold && stored < threshold;
        var deficit = isActive ? Math.Min(unstored, Math.Max(0, threshold - stored)) : 0.0;
        var larder = CorpseLarderRules.SelectCorpseLarder(v).Kind.Length != 0;
        return r with { Active = isActive || larder, Larder = larder, Target = threshold, Deficit = deficit };
    }

    /// <summary>Existing storage with room leaves hauling to vanilla; otherwise production may protect the nutrition at risk.</summary>
    public static FoodStorageMethodKind SelectFoodStorageMethod(FoodStorageReview review, IReadOnlyList<FoodStorageSite> sites)
    {
        if (!review.Active || review.Deficit <= 0)
        {
            return FoodStorageMethodKind.Recovered;
        }
        DomainException.Require(FoodForecastRules.FoodNumber(review.Deficit), "invalid food storage deficit");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            DomainException.Require(FoodForecastRules.FoodId(site.Id) && seen.Add(site.Id) && site.Capacity >= 0, "invalid food storage site");
        }
        return sites.Any(s => s.Capacity > 0) ? FoodStorageMethodKind.Stuck : FoodStorageMethodKind.Produce;
    }
}
