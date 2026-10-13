namespace RimGovernor.Brain.Policy;

// Go: candidate.go

/// <summary>How a candidate obtains its yields.</summary>
public enum CandidateKind
{
    [WireName("")]
    None,
    Forage,
    Hunt,
    Slaughter,
    Crop,
    AnimalProduct,
    Fishing,
    Trade,
    Tame,
    AnimalBuy,
    Corpse,
    Reserve,
    Cook,
    Loot,
    Salvage,
    Mining,
    Produce,
    DeepDrill,
    Chop,
    Harvest,
}

/// <summary>
/// Where a candidate stands in its life: Designated is committed but not yet delivering, Delivering is observed
/// delivering, Closed is neither. Which states earn credit is the ranker's and ledger's rule, never the type's.
/// </summary>
public enum CandidateState
{
    [WireName("")]
    None,
    Designated,
    Delivering,
    Closed,
}

/// <summary>A hazard that discounts a candidate's yield.</summary>
public enum CandidateRiskKind
{
    [WireName("")]
    None,
    Blight,
    Fallout,
    Frost,
    Revenge,
}

/// <summary>One good a candidate yields. Headroom is known destination capacity accepting this exact output, in item units.</summary>
public sealed record CandidateYield
{
    public ResourceKey Good { get; init; } = default!;

    /// <summary>The steady rate.</summary>
    public Fact<double> PerDay { get; init; }

    /// <summary>Bounds the total a finite source holds, so a one-shot source is a lead-0 candidate with a stock cap and no rate.</summary>
    public Fact<long> StockCap { get; init; }

    public double UnitValue { get; init; }

    public Fact<long> Headroom { get; init; }
}

/// <summary>One hazard's weight in [0,1]; summed risks discount the yield, clamped at 100%.</summary>
public sealed record CandidateRisk(CandidateRiskKind Kind, double Weight);

/// <summary>A named number the candidate's builder reports for explanations.</summary>
public sealed record CandidateTerm(string Name, double Value);

/// <summary>What establishing a candidate costs before it yields: labor-equivalent ticks and resources.</summary>
public sealed record CandidateCost
{
    public Fact<double> LaborTicks { get; init; }

    public EquatableArray<ResourceQuantity> Resources { get; init; }
}

/// <summary>
/// The one type every supply channel is described by: a food channel, a loot or mining source, a bench bill, a
/// trader. Yields are independent of other candidates' (a Cook candidate yields the incremental nutrition of
/// conversion, not the raw input another candidate counts).
/// </summary>
public sealed record SupplyCandidate
{
    public CandidateKind Kind { get; init; }

    public string Id { get; init; } = "";

    public Fact<CandidateState> State { get; init; }

    /// <summary>The set of goods this candidate delivers (a deer yields nutrition and leather).</summary>
    public EquatableArray<CandidateYield> Yields { get; init; }

    public Fact<double> LeadDays { get; init; }

    /// <summary>The steady work a delivering candidate takes.</summary>
    public Fact<double> LaborPerDay { get; init; }

    /// <summary>The work and resources to establish it.</summary>
    public CandidateCost UpfrontCost { get; init; } = new();

    public EquatableArray<CandidateRisk> Risk { get; init; }

    /// <summary>Native path length.</summary>
    public Fact<double> PathDistance { get; init; }

    /// <summary>The nearest source cell to the colony centre, which ranks equal-lead fishing regions nearest first.</summary>
    public Fact<double> DistanceSquared { get; init; }

    /// <summary>The yields must be carried.</summary>
    public bool NeedsHaul { get; init; }

    /// <summary>The observed carrying capacity. A zero value with NeedsHaul is invalid.</summary>
    public long UnitsPerTrip { get; init; }

    public EquatableArray<CandidateTerm> Terms { get; init; }

    /// <summary>A formation hunt's animals, sorted.</summary>
    public EquatableArray<string> Prey { get; init; }

    /// <summary>
    /// Names the ledger counter group that counts the candidate's deliveries ("crop:&lt;zone&gt;",
    /// "fish:&lt;x,z&gt;", "forage:&lt;def&gt;", "animal_product:&lt;race&gt;"); empty for one the ledger does not count.
    /// </summary>
    public string Source { get; init; } = "";

    /// <summary>Whether the candidate is observed delivering; unknown when its state is.</summary>
    public Fact<bool> Open()
    {
        if (!State.TryGetValue(out var state))
        {
            return Unknown<bool>();
        }

        return Known(state == CandidateState.Delivering);
    }

    /// <summary>The candidate's nutrition yield: the first yield when it is nutrition, else the zero (unknown) yield.</summary>
    public CandidateYield Nutrition()
    {
        if (Yields.Count > 0 && Yields[0].Good == NutritionKey)
        {
            return Yields[0];
        }

        return new CandidateYield();
    }

    /// <summary>This candidate with its nutrition rate replaced; a shared candidate is never edited in place.</summary>
    public SupplyCandidate WithNutritionPerDay(Fact<double> perDay)
    {
        if (Yields.Count == 0 || Yields[0].Good != NutritionKey)
        {
            return this;
        }

        var yields = Yields.Items.ToBuilder();
        yields[0] = yields[0] with { PerDay = perDay };
        return this with { Yields = yields.ToImmutable() };
    }

    /// <summary>The goods a food candidate yields beside its nutrition (a hunted deer's leather).</summary>
    public IReadOnlyList<CandidateYield> Products()
    {
        if (Yields.Count > 1 && Yields[0].Good == NutritionKey)
        {
            return Yields.Skip(1).ToArray();
        }

        return [];
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The good a food candidate yields.</summary>
    public static readonly Resource CandidateNutrition = new("Nutrition");

    /// <summary>
    /// Starts a food candidate: nutrition is its first yield, at <paramref name="perDay"/> with no known stock
    /// bound. The rest of the yields are its products.
    /// </summary>
    public static SupplyCandidate FoodCandidate(CandidateKind kind, string id, Fact<double> perDay) =>
        new()
        {
            Kind = kind,
            Id = id,
            Yields = EquatableArray.From([new CandidateYield { Good = NutritionKey, PerDay = perDay, StockCap = Unknown<long>() }]),
        };

    /// <summary>A food candidate's standing: Delivering when open, else Designated when committed, else Closed; unknown when open is.</summary>
    public static Fact<CandidateState> FoodState(Fact<bool> open, Fact<bool> designated)
    {
        var known = open.TryGetValue(out var isOpen);
        designated.TryGetValue(out var committed);
        if (!known)
        {
            return Unknown<CandidateState>();
        }

        if (isOpen)
        {
            return Known(CandidateState.Delivering);
        }

        if (committed)
        {
            return Known(CandidateState.Designated);
        }

        return Known(CandidateState.Closed);
    }

    /// <summary>Count units of a finite source's output: lead-0 stock with no rate.</summary>
    public static CandidateYield SourceYield(ResourceKey key, long count, double unitValue, Fact<long> headroom) =>
        new()
        {
            Good = key,
            StockCap = Known(count),
            UnitValue = unitValue,
            Headroom = headroom,
        };

    /// <summary>
    /// A one-shot source (loot, salvage, a deposit, a bill, a trader): lead 0, the whole source's labor its
    /// upfront cost and no state beyond Closed (not committed). Hunt revenge risk stays inside labor.
    /// </summary>
    public static SupplyCandidate SourceCandidate(
        CandidateKind kind,
        string id,
        Fact<double> labor,
        Fact<double> path,
        bool haul,
        long unitsPerTrip,
        params CandidateYield[] yields
    ) =>
        new()
        {
            Kind = kind,
            Id = id,
            State = Known(CandidateState.Closed),
            Yields = EquatableArray.From(yields),
            LeadDays = Known(0.0),
            UpfrontCost = new CandidateCost { LaborTicks = labor },
            PathDistance = path,
            NeedsHaul = haul,
            UnitsPerTrip = unitsPerTrip,
        };
}
