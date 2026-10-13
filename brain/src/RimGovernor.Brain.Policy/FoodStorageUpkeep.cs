using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Wraps an observed <see cref="FoodStock"/> (reused as-is from FoodForecast.cs). Whether the stock counts as
/// adequately stored is derived per review from its Roofed/TemperatureC/RotTicks facts by <see cref="Stored"/>,
/// so the storage and refrigeration policies share one definition. (Go <c>FoodStorageStock</c>.)
/// </summary>
public sealed record FoodStorageStock(FoodStock Stock)
{
    /// <summary>
    /// Go <c>FoodStorageStock.stored</c>: whether a perishable stock sits in adequate storage: roofed, and either
    /// chilled to chilledMaxC or with at least SafeRotDays of runway left at its current temperature. A
    /// roofed-but-warm stockpile is adequate for food that will be eaten long before it rots; only warm food
    /// close to spoiling is at risk. Unknown when any needed fact is unknown.
    /// </summary>
    internal Fact<bool> Stored(double chilledMaxC, FoodStoragePolicy p)
    {
        if (!Stock.Roofed.TryGetValue(out var roofed))
        {
            return Unknown<bool>();
        }
        if (!roofed)
        {
            return Known(false);
        }
        var tk = Stock.TemperatureC.TryGetValue(out var temperature);
        var kk = Stock.RotTicks.TryGetValue(out var ticks);
        if (!tk || !kk)
        {
            return Unknown<bool>();
        }
        return Known(temperature <= chilledMaxC || ticks >= p.SafeRotDays * GameTime.TicksPerDay);
    }
}

/// <summary>
/// The complete per-cycle census MaintainFoodStorage reviews. An unknown Stocks fact preserves the latch, the
/// same posture MedicalReserveObservation's Items/Resources facts take in ReviewMedicalReserve.
/// (Go <c>FoodStorageObservation</c>.)
/// </summary>
public sealed record FoodStorageObservation
{
    public Fact<IReadOnlyList<FoodStorageStock>> Stocks { get; init; }

    public Fact<FoodLarder> Larder { get; init; }

    /// <summary>
    /// The storage temperature at or below which perishable stock counts as refrigerated: the catalog's
    /// full_rot_rate_c, the temperature where the game's rot rate reaches its full rate.
    /// </summary>
    public double ChilledMaxC { get; init; }

    /// <summary>Go <c>FoodStorageObservation.Validate</c>: rejects malformed or duplicate stock rows; an unknown census is valid. Throws <see cref="DomainException"/>.</summary>
    public void Validate()
    {
        if (!Stocks.TryGetValue(out var stocks))
        {
            return;
        }
        DomainException.Require(PolicyFunctions.Celsius(ChilledMaxC), "invalid food storage facts");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in stocks)
        {
            var s = entry.Stock;
            DomainException.Require(PolicyFunctions.FoodId(s.Id) && seen.Add(s.Id), "invalid food storage facts");
            DomainException.Require(!(s.Nutrition.TryGetValue(out var nutrition) && !PolicyFunctions.FoodNumber(nutrition)), "invalid food storage facts");
            DomainException.Require(!(s.RotTicks.TryGetValue(out var ticks) && ticks < 0), "invalid food storage facts");
            DomainException.Require(!(s.TemperatureC.TryGetValue(out var temperature) && !PolicyFunctions.Celsius(temperature)), "invalid food storage facts");
        }
    }
}

/// <summary>
/// Names the hysteresis band ReviewFoodStorage applies to the fraction of at-risk perishable nutrition that must
/// be adequately stored, the same MinimumPerColonist/TargetPerColonist shape MedicalReservePolicy uses, plus a
/// floor below which a tiny at-risk amount never activates the deficit (avoiding thrash over a single dropped
/// ration). (Go <c>FoodStoragePolicy</c>.)
/// </summary>
public sealed record FoodStoragePolicy
{
    public double MinimumStoredFraction { get; init; }

    public double TargetStoredFraction { get; init; }

    public double AtRiskNutritionThreshold { get; init; }

    /// <summary>Our own tuning (the game defines no safe days): the rot runway that makes warm roofed stock acceptable anyway.</summary>
    public double SafeRotDays { get; init; }

    /// <summary>The measured storage temperature MaintainRefrigeration releases at (hysteresis under FoodStorageObservation.ChilledMaxC).</summary>
    public double ChilledExitC { get; init; }

    /// <summary>The setpoint MaintainRefrigeration asks a cooler for.</summary>
    public double FreezerTargetC { get; init; }

    internal bool Valid =>
        PolicyFunctions.FoodNumber(MinimumStoredFraction) && PolicyFunctions.FoodNumber(TargetStoredFraction) && PolicyFunctions.FoodNumber(AtRiskNutritionThreshold)
        && PolicyFunctions.FoodNumber(SafeRotDays) && PolicyFunctions.Celsius(ChilledExitC) && PolicyFunctions.Celsius(FreezerTargetC)
        && FreezerTargetC <= ChilledExitC
        && MinimumStoredFraction < TargetStoredFraction && TargetStoredFraction <= 1;
}

/// <summary>
/// A latching deficit review, the same shape MedicalReserveReview uses: Active only flips off once storage
/// recovers past the (higher) TargetStoredFraction, and an unavailable read preserves the prior Active value
/// rather than guessing. (Go <c>FoodStorageReview</c>.)
/// </summary>
public sealed record FoodStorageReview
{
    public bool Active { get; init; }

    public Fact<double> StoredNutrition { get; init; }

    public Fact<double> UnstoredNutrition { get; init; }

    public Fact<double> TotalNutrition { get; init; }

    public Fact<double> Target { get; init; }

    public Fact<double> Deficit { get; init; }
}

public enum FoodStorageMethodKind
{
    [WireName("")]
    None = 0,

    [WireName("unknown")]
    Unknown,

    [WireName("recovered")]
    Recovered,

    /// <summary>A covered site already has room, so the stock is unstored for a reason FoodStorageStuckReason names, not for lack of space.</summary>
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
/// (Go <c>FoodStorageSite</c>.)
/// </summary>
public sealed record FoodStorageSite
{
    public string Id { get; init; } = "";

    public Fact<long> Capacity { get; init; }
}

/// <summary>
/// The outcome SelectFoodStorageMethod proposes: Stuck when a covered/enclosed site already has room (no order
/// is planned; vanilla hauling owns the move), or, when no site has room, a StockTarget-style production bill
/// request for more preserved/non-perishable food (Resource/Target only; bench and recipe selection is the
/// routine planner's job, the same deferral GearProduce/MaintainResource already use). (Go <c>FoodStorageMethod</c>.)
/// </summary>
public sealed record FoodStorageMethod
{
    public FoodStorageMethodKind Kind { get; init; }

    public MethodId Id { get; init; }

    public Resource Resource { get; init; }

    public long Target { get; init; }
}

/// <summary>
/// Carries everything SelectFoodStorageMethod needs: the latched Review, the observed candidate Sites census, the
/// one Resource name to fall back to producing (an operator-declared preserved/non-perishable food definition, a
/// config-only choice), and Seen method IDs already dispatched this cycle so a resolved relocation or bill is
/// never proposed twice. (Go <c>FoodStoragePlanningRequest</c>.)
/// </summary>
public sealed record FoodStoragePlanningRequest
{
    public FoodStorageReview Review { get; init; } = new();

    public Fact<IReadOnlyList<FoodStorageSite>> Sites { get; init; }

    public Resource Resource { get; init; }

    public IReadOnlyList<MethodId> Seen { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>Go <c>FoodStorageStocks</c>: lifts a food-supply census into the storage-upkeep census. chilledMaxC is the catalog's full-rot-rate temperature.</summary>
    public static FoodStorageObservation FoodStorageStocks(FoodSupply supply, double chilledMaxC) =>
        new()
        {
            Stocks = Known<IReadOnlyList<FoodStorageStock>>([.. supply.Stocks.Concat(supply.Barred).Select(stock => new FoodStorageStock(stock))]),
            Larder = supply.Larder,
            ChilledMaxC = chilledMaxC,
        };

    /// <summary>Accepts any finite temperature, below zero included.</summary>
    internal static bool Celsius(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

    /// <summary>Go <c>FoodStorageUnstored</c>: whether a stock is known perishable, not yet rotted and known not adequately stored under p and the census's chilledMaxC.</summary>
    public static bool FoodStorageUnstored(FoodStorageStock s, double chilledMaxC, FoodStoragePolicy p)
    {
        var pk = s.Stock.Perishable.TryGetValue(out var perishable);
        var tk = s.Stock.RotTicks.TryGetValue(out var ticks);
        if (!pk || !perishable || !tk || ticks <= 0)
        {
            return false;
        }
        return s.Stored(chilledMaxC, p).TryGetValue(out var stored) && !stored;
    }

    public static FoodStoragePolicy DefaultFoodStoragePolicy() =>
        new()
        {
            MinimumStoredFraction = 0.5,
            TargetStoredFraction = 0.9,
            AtRiskNutritionThreshold = 5,
            SafeRotDays = 5,
            ChilledExitC = 5,
            FreezerTargetC = -5,
        };

    /// <summary>
    /// Go <c>ReviewFoodStorage</c>: tallies perishable, not-yet-rotted nutrition across every observed stock,
    /// splitting it into stored and unstored piles. It goes active when the unstored pile clears
    /// AtRiskNutritionThreshold and the stored share of total perishable nutrition falls under the current
    /// threshold (entry MinimumStoredFraction, or the higher TargetStoredFraction once already active), and
    /// clears once storage recovers past that same higher bar. Non-perishable and already-rotted stock never
    /// contributes: it is not at risk of being lost to inadequate storage. An unknown fact anywhere in the census
    /// preserves the latch untouched, matching ReviewMedicalReserve. Throws <see cref="DomainException"/> where
    /// Go returned an error.
    /// </summary>
    public static FoodStorageReview ReviewFoodStorage(FoodStorageObservation v, bool active, FoodStoragePolicy p)
    {
        var r = new FoodStorageReview { Active = active };
        DomainException.Require(p.Valid, "invalid food storage facts or thresholds");
        try
        {
            v.Validate();
        }
        catch (DomainException)
        {
            throw new DomainException("invalid food storage facts or thresholds");
        }
        if (!v.Stocks.TryGetValue(out var stocks))
        {
            return r;
        }
        double stored = 0;
        double unstored = 0;
        double total = 0;
        foreach (var entry in stocks)
        {
            var s = entry.Stock;
            var nk = s.Nutrition.TryGetValue(out var nutrition);
            var pk = s.Perishable.TryGetValue(out var perishable);
            if (!nk || !pk || !perishable)
            {
                continue;
            }
            var tk = s.RotTicks.TryGetValue(out var ticks);
            if (!tk || ticks <= 0)
            {
                continue;
            }
            if (!entry.Stored(v.ChilledMaxC, p).TryGetValue(out var storedFact))
            {
                return r;
            }
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
        DomainException.Require(FoodNumber(stored) && FoodNumber(unstored) && FoodNumber(total), "invalid food storage facts or thresholds");
        r = r with { StoredNutrition = Known(stored), UnstoredNutrition = Known(unstored), TotalNutrition = Known(total) };
        if (total == 0)
        {
            return r with { Active = false, Target = Known(0.0), Deficit = Known(0.0) };
        }
        var fraction = active ? p.TargetStoredFraction : p.MinimumStoredFraction;
        var threshold = fraction * total;
        var isActive = unstored >= p.AtRiskNutritionThreshold && stored < threshold;
        var deficit = isActive ? Math.Min(unstored, Math.Max(0, threshold - stored)) : 0.0;
        r = r with { Active = isActive, Target = Known(threshold), Deficit = Known(deficit) };
        var larder = SelectCorpseLarder(v);
        return r with { Active = r.Active || larder.Kind != default };
    }

    private static MethodId FoodStorageMethodId(string kind, string resource, long target)
    {
        // Mirrors Go's json.Marshal of struct{Resource Resource; Target int64}.
        var json = $"{{\"Resource\":{JsonSerializer.Serialize(resource)},\"Target\":{target}}}";
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + json));
        return new MethodId($"food-storage-{kind}-{Convert.ToHexStringLower(sum.AsSpan(0, 16))}");
    }

    /// <summary>
    /// Go <c>SelectFoodStorageMethod</c>: issues no game orders and reserves nothing; the shared method
    /// admission must recheck site capacity and production costs against concurrent plans before committing.
    /// With the deficit active, the first candidate site (by deterministic sort) with known spare capacity makes
    /// the outcome Stuck. Only once every site is exhausted does it fall back to Produce. Throws
    /// <see cref="DomainException"/> where Go returned an error.
    /// </summary>
    public static FoodStorageMethod SelectFoodStorageMethod(FoodStoragePlanningRequest r)
    {
        if (!r.Review.Active)
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Recovered };
        }
        if (!r.Review.Deficit.TryGetValue(out var deficit))
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Unknown };
        }
        if (deficit <= 0)
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Recovered };
        }
        DomainException.Require(FoodNumber(deficit) && deficit <= 1e9, "invalid food storage deficit");
        var seen = new HashSet<MethodId>();
        foreach (var id in r.Seen)
        {
            DomainException.Require(FoodId(id.Value) && seen.Add(id), "invalid food storage method history");
        }
        if (!r.Sites.TryGetValue(out var sites))
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Unknown };
        }
        var candidates = sites.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        var seenSite = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in candidates)
        {
            DomainException.Require(FoodId(site.Id) && seenSite.Add(site.Id), "invalid or duplicate food storage site");
        }
        var sawUnknownCapacity = false;
        foreach (var site in candidates)
        {
            if (!site.Capacity.TryGetValue(out var capacity))
            {
                sawUnknownCapacity = true;
                continue;
            }
            DomainException.Require(capacity >= 0, "invalid food storage site capacity");
            if (capacity <= 0)
            {
                continue;
            }
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Stuck };
        }
        if (sawUnknownCapacity)
        {
            return new FoodStorageMethod { Kind = FoodStorageMethodKind.Unknown };
        }
        if (!ValidResource(r.Resource))
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
        return new FoodStorageMethod { Kind = FoodStorageMethodKind.Produce, Id = produceId, Resource = r.Resource, Target = target };
    }
}
