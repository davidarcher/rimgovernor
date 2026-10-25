using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public FoodLarder Larder { get; init; } = new();

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
    [WireName("")]
    None = 0,


    [WireName("recovered")]
    Recovered,

    /// <summary>A covered site already has room, so the stock is unstored despite available capacity.</summary>
    [WireName("stuck")]
    Stuck,

    [WireName("produce")]
    Produce,

    [WireName("no_eligible_method")]
    Blocked,
}

/// <summary>
/// A candidate covered/enclosed stockpile SelectFoodStorageMethod may relocate at-risk stock into. Capacity is
/// the site's remaining nutrition-equivalent room; it is a plain observed fact, not a reservation.
/// </summary>
public sealed record FoodStorageSite
{
    public string Id { get; init; } = "";

    public long Capacity { get; init; }
}

/// <summary>
/// The outcome SelectFoodStorageMethod proposes: Stuck when a covered/enclosed site already has room (no order
/// is planned; vanilla hauling owns the move), or, when no site has room, a StockTarget-style production bill
/// request for more preserved/non-perishable food (Resource/Target only; bench and recipe selection is the
/// routine planner's job, the same deferral GearProduce/the economy/resource Need already use).
/// </summary>
public sealed record FoodStorageMethod
{
    public FoodStorageMethodKind Kind { get; init; }

    public Resource Resource { get; init; }

    public long Target { get; init; }
}

/// <summary>
/// Carries everything SelectFoodStorageMethod needs: the latched Review, the observed candidate Sites census, the
/// one Resource name to fall back to producing (an operator-declared preserved/non-perishable food definition, a
/// config-only choice), and Seen method IDs already dispatched this cycle so a resolved relocation or bill is
/// never proposed twice.
/// </summary>
public sealed record FoodStoragePlanningRequest
{
    public FoodStorageReview Review { get; init; } = new();

    public EquatableArray<FoodStorageSite> Sites { get; init; }


    public Resource Resource { get; init; }

    public IReadOnlyList<MethodId> Seen { get; init; } = [];
}

public static class FoodStorageUpkeepRules
{
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
            DomainException.Require(!(total > double.MaxValue - nutrition), "invalid food storage facts or thresholds");
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

    private static MethodId FoodStorageMethodId(string kind, string resource, long target)
    {
        // The JSON of struct{Resource Resource; Target int64}.
        var json = $"{{\"Resource\":{JsonSerializer.Serialize(resource)},\"Target\":{target}}}";
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + json));
        return new MethodId($"food-storage-{kind}-{Convert.ToHexStringLower(sum.AsSpan(0, 16))}");
    }

    /// <summary>
    /// Issues no game orders and reserves nothing; the shared method
    /// admission must recheck site capacity and production costs against concurrent plans before committing.
    /// With the deficit active, the first candidate site (by deterministic sort) with known spare capacity makes
    /// the outcome Stuck. Only once every site is exhausted does it fall back to Produce. Throws
    /// <see cref="DomainException"/>.
    /// </summary>
    public static FoodStorageMethod SelectFoodStorageMethod(FoodStoragePlanningRequest r)
    {
        if (!r.Review.Active)
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Recovered };
        }
        var deficit = r.Review.Deficit;
        if (deficit <= 0)
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Recovered };
        }
        DomainException.Require(FoodForecastRules.FoodNumber(deficit) && deficit <= 1e9, "invalid food storage deficit");
        var seen = new HashSet<MethodId>();
        foreach (var id in r.Seen)
        {
            DomainException.Require(FoodForecastRules.FoodId(id.Value) && seen.Add(id), "invalid food storage method history");
        }
        var candidates = r.Sites.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        var seenSite = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in candidates)
        {
            DomainException.Require(FoodForecastRules.FoodId(site.Id) && seenSite.Add(site.Id), "invalid or duplicate food storage site");
        }
        foreach (var site in candidates)
        {
            DomainException.Require(site.Capacity >= 0, "invalid food storage site capacity");
            if (site.Capacity > 0)
            {
                return new FoodStorageMethod { Kind = FoodStorageMethodKind.Stuck };
            }
        }
        if (!AdmissionRules.ValidResource(r.Resource))
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Blocked };
        }
        var target = Math.Max(1L, (long)Math.Ceiling(deficit));
        if (target > int.MaxValue)
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Blocked };
        }
        var produceId = FoodStorageMethodId("produce", r.Resource.Value, target);
        if (seen.Contains(produceId))
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Blocked };
        }
        return new FoodStorageMethod { Kind = FoodStorageMethodKind.Produce, Resource = r.Resource, Target = target };
    }
}
