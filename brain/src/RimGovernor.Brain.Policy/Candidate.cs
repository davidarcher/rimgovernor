namespace RimGovernor.Brain.Policy;


/// <summary>How a candidate obtains its yields.</summary>
public enum CandidateKind
{
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
    Designated,
    Delivering,
    Closed,
}

/// <summary>A hazard that discounts a candidate's yield.</summary>
public enum CandidateRiskKind
{
    Blight,
    Fallout,
    Frost,
    Revenge,
}

/// <summary>How much of a good a yield delivers: a steady rate, a finite stock, both, or a rate its producer cannot price.</summary>
public abstract record YieldSupply
{
    private protected YieldSupply() { }

    /// <summary>A steady rate with no stock bound.</summary>
    public sealed record Steady(double PerDay) : YieldSupply;

    /// <summary>A steady rate drawn from a finite stock: the rate is bounded by the stock spread over the demand's window.</summary>
    public sealed record Bounded(double PerDay, long Cap) : YieldSupply;

    /// <summary>A finite stock with no rate: a one-shot source is a lead-0 candidate with a stock and no rate.</summary>
    public sealed record Stock(long Count) : YieldSupply;

    /// <summary>The producer cannot price the rate (a fishing body without a working fisher): the plan reports the good's per-day rate missing.</summary>
    public sealed record Unpriced : YieldSupply;
}

/// <summary>Where a yield goes once delivered.</summary>
public abstract record YieldStorage
{
    private protected YieldStorage() { }

    /// <summary>The output is used where it is made or arrives (food a channel delivers, a bench's product, a caravan's goods): no haul is charged.</summary>
    public sealed record InPlace : YieldStorage;

    /// <summary>Storage accepting this exact output, in item units.</summary>
    public sealed record Accepts(long Units) : YieldStorage;

    /// <summary>The storage census was not read: a hauled yield is credited no units.</summary>
    public sealed record Unread : YieldStorage;
}

/// <summary>When a candidate first delivers.</summary>
public abstract record CandidateLead
{
    private protected CandidateLead() { }

    /// <summary>Days until the first delivery; 0 is now.</summary>
    public sealed record Days(double Value) : CandidateLead;

    /// <summary>No delivery can be scheduled yet (fishing research without progress): the plan reports lead_days missing.</summary>
    public sealed record Unscheduled : CandidateLead;
}

/// <summary>The steady work a candidate takes.</summary>
public abstract record CandidateWork
{
    private protected CandidateWork() { }

    /// <summary>Work ticks a day while it delivers.</summary>
    public sealed record PerDay(double Ticks) : CandidateWork;

    /// <summary>A one-shot source: all its work is the upfront cost, and its distance prices it.</summary>
    public sealed record OneShot : CandidateWork;

    /// <summary>The producer cannot price the work (a fishing body without a working fisher): the plan reports work_per_day missing.</summary>
    public sealed record Unpriced : CandidateWork;
}

/// <summary>The walk to a candidate's source.</summary>
public abstract record CandidatePath
{
    private protected CandidatePath() { }

    /// <summary>The native path length.</summary>
    public sealed record Walk(double Distance) : CandidatePath;

    /// <summary>
    /// No walked path: a steady channel priced by its labor, or a source native found no path to. The plan reports
    /// path_distance missing where the distance prices the candidate (a hauled or one-shot source).
    /// </summary>
    public sealed record Unwalked : CandidatePath;
}

/// <summary>How near a candidate's source lies to the colony centre.</summary>
public abstract record SourceNearness
{
    private protected SourceNearness() { }

    /// <summary>The nearest source cell's squared distance to the colony centre, which ranks equal-lead fishing regions nearest first.</summary>
    public sealed record Measured(double DistanceSquared) : SourceNearness;

    /// <summary>The kind does not rank by nearness.</summary>
    public sealed record Unmeasured : SourceNearness;
}

/// <summary>One good a candidate yields.</summary>
public sealed record CandidateYield
{
    public required ResourceKey Good { get; init; }

    public required YieldSupply Supply { get; init; }

    public required double UnitValue { get; init; }

    public required YieldStorage Storage { get; init; }
}

/// <summary>One hazard's weight in [0,1]; summed risks discount the yield, clamped at 100%.</summary>
public sealed record CandidateRisk(CandidateRiskKind Kind, double Weight);

/// <summary>A named number the candidate's builder reports for explanations.</summary>
public sealed record CandidateTerm(string Name, double Value);

/// <summary>What establishing a candidate costs before it yields: labor-equivalent ticks and resources.</summary>
public sealed record CandidateCost
{
    public required double LaborTicks { get; init; }

    public EquatableArray<ResourceQuantity> Resources { get; init; }
}

/// <summary>
/// The one type every supply channel is described by: a food channel, a loot or mining source, a bench bill, a
/// trader. Yields are independent of other candidates' (a Cook candidate yields the incremental nutrition of
/// conversion, not the raw input another candidate counts).
/// </summary>
public sealed record SupplyCandidate
{
    public required CandidateKind Kind { get; init; }

    public required string Id { get; init; }

    public required CandidateState State { get; init; }

    /// <summary>The set of goods this candidate delivers (a deer yields nutrition and leather).</summary>
    public required EquatableArray<CandidateYield> Yields { get; init; }

    public required CandidateLead Lead { get; init; }

    /// <summary>The steady work a delivering candidate takes.</summary>
    public required CandidateWork Work { get; init; }

    /// <summary>The work and resources to establish it.</summary>
    public required CandidateCost UpfrontCost { get; init; }

    public EquatableArray<CandidateRisk> Risk { get; init; }

    public required CandidatePath Path { get; init; }

    public required SourceNearness Nearness { get; init; }

    /// <summary>The yields must be carried.</summary>
    public required bool NeedsHaul { get; init; }

    /// <summary>The observed carrying capacity. A zero value with NeedsHaul is invalid.</summary>
    public required long UnitsPerTrip { get; init; }

    public EquatableArray<CandidateTerm> Terms { get; init; }

    /// <summary>A formation hunt's animals, sorted.</summary>
    public EquatableArray<string> Prey { get; init; }

    /// <summary>A formation hunt's selected gunners, sorted: the only pawns the hunt may send.</summary>
    public EquatableArray<string> Hunters { get; init; }

    /// <summary>
    /// A formation hunt's retriever (at most one), sorted: a gunner who leaves the encounter to haul, or a colonist outside
    /// it who hauls after it. Not drafted; held for the trip (WorkResources claims), the hauling itself native work.
    /// </summary>
    public EquatableArray<string> Retrievers { get; init; }

    /// <summary>
    /// Names the ledger counter group that counts the candidate's deliveries ("crop:&lt;zone&gt;",
    /// "fish:&lt;x,z&gt;", "forage:&lt;def&gt;", "animal_product:&lt;race&gt;"); empty for one the ledger does not count.
    /// </summary>
    public string Source { get; init; } = "";

    /// <summary>Whether the candidate is observed delivering.</summary>
    public bool Open() => State == CandidateState.Delivering;

    /// <summary>The candidate's nutrition yield: the first yield when it is nutrition, else none.</summary>
    public CandidateYield? Nutrition()
    {
        if (Yields.Count > 0 && Yields[0].Good == SupplyPlanRules.NutritionKey)
        {
            return Yields[0];
        }

        return null;
    }

    /// <summary>This candidate with its nutrition supply replaced; a shared candidate is never edited in place.</summary>
    public SupplyCandidate WithNutritionSupply(YieldSupply supply)
    {
        if (Yields.Count == 0 || Yields[0].Good != SupplyPlanRules.NutritionKey)
        {
            return this;
        }

        var yields = Yields.Items.ToBuilder();
        yields[0] = yields[0] with { Supply = supply };
        return this with { Yields = yields.ToImmutable() };
    }

    /// <summary>The goods a food candidate yields beside its nutrition (a hunted deer's leather).</summary>
    public IReadOnlyList<CandidateYield> Products()
    {
        if (Yields.Count > 1 && Yields[0].Good == SupplyPlanRules.NutritionKey)
        {
            return Yields.Skip(1).ToArray();
        }

        return [];
    }
}

public static class CandidateRules
{
    /// <summary>The good a food candidate yields.</summary>
    public static readonly Resource CandidateNutrition = new("Nutrition");

    /// <summary>
    /// Starts a food candidate: nutrition is its first yield at <paramref name="supply"/>, delivered where it is eaten.
    /// The rest of the yields are its products. It has no upfront cost, walk or nearness until the builder sets them.
    /// </summary>
    public static SupplyCandidate FoodCandidate(CandidateKind kind, string id, YieldSupply supply, CandidateState state, CandidateLead lead, CandidateWork work) =>
        new()
        {
            Kind = kind,
            Id = id,
            State = state,
            Yields = EquatableArray.From([NutritionYield(supply)]),
            Lead = lead,
            Work = work,
            UpfrontCost = new CandidateCost { LaborTicks = 0 },
            Path = new CandidatePath.Unwalked(),
            Nearness = new SourceNearness.Unmeasured(),
            NeedsHaul = false,
            UnitsPerTrip = 0,
        };

    /// <summary>A nutrition yield at <paramref name="supply"/>, eaten where it is delivered.</summary>
    public static CandidateYield NutritionYield(YieldSupply supply) =>
        new() { Good = SupplyPlanRules.NutritionKey, Supply = supply, UnitValue = 0, Storage = new YieldStorage.InPlace() };

    /// <summary>A food product yield (a hunted deer's leather) at a steady rate, used where it is delivered.</summary>
    public static CandidateYield ProductYield(ResourceKey good, double perDay) =>
        new() { Good = good, Supply = new YieldSupply.Steady(perDay), UnitValue = 0, Storage = new YieldStorage.InPlace() };

    /// <summary>A food candidate's standing: Delivering when open, else Designated when committed, else Closed.</summary>
    public static CandidateState FoodState(bool open, bool designated)
    {
        if (open)
        {
            return CandidateState.Delivering;
        }

        return designated ? CandidateState.Designated : CandidateState.Closed;
    }

    /// <summary>The steady nutrition rate of a candidate whose nutrition yield has one.</summary>
    public static bool NutritionRate(this SupplyCandidate c, out double perDay)
    {
        if (c.Nutrition() is { } y)
        {
            return y.RateOf(out perDay);
        }

        perDay = 0;
        return false;
    }

    /// <summary>The daily work of a candidate priced per day; false for a one-shot or unpriced candidate.</summary>
    public static bool WorkRate(this SupplyCandidate c, out double ticks)
    {
        if (c.Work is CandidateWork.PerDay w)
        {
            ticks = w.Ticks;
            return true;
        }

        ticks = 0;
        return false;
    }

    /// <summary>The steady rate of a yield that has one: a steady or a bounded rate.</summary>
    public static bool RateOf(this CandidateYield y, out double perDay)
    {
        var supply = y.Supply;
        if (supply is YieldSupply.Steady s)
        {
            perDay = s.PerDay;
            return true;
        }

        if (supply is YieldSupply.Bounded b)
        {
            perDay = b.PerDay;
            return true;
        }

        perDay = 0;
        return false;
    }

    /// <summary>A supply with its rate scaled by <paramref name="factor"/>; a stock and an unpriced supply stay as they are.</summary>
    public static YieldSupply ScaledRate(this YieldSupply supply, double factor)
    {
        if (supply is YieldSupply.Steady s)
        {
            return new YieldSupply.Steady(s.PerDay * factor);
        }

        if (supply is YieldSupply.Bounded b)
        {
            return new YieldSupply.Bounded(b.PerDay * factor, b.Cap);
        }

        return supply;
    }

    /// <summary>The finite stock of a yield that has one: a one-shot stock or a bounded rate's stock.</summary>
    public static bool StockOf(this CandidateYield y, out long stock)
    {
        if (y.Supply is YieldSupply.Stock s)
        {
            stock = s.Count;
            return true;
        }

        if (y.Supply is YieldSupply.Bounded b)
        {
            stock = b.Cap;
            return true;
        }

        stock = 0;
        return false;
    }

    /// <summary>Count units of a finite source's output: lead-0 stock with no rate.</summary>
    public static CandidateYield SourceYield(ResourceKey key, long count, double unitValue, YieldStorage storage) =>
        new()
        {
            Good = key,
            Supply = new YieldSupply.Stock(count),
            UnitValue = unitValue,
            Storage = storage,
        };

    /// <summary>
    /// A one-shot source (loot, salvage, a deposit, a bill, a trader): lead 0, the whole source's labor its
    /// upfront cost and no state beyond Closed (not committed). Hunt revenge risk stays inside labor.
    /// </summary>
    public static SupplyCandidate SourceCandidate(
        CandidateKind kind,
        string id,
        double labor,
        CandidatePath path,
        bool haul,
        long unitsPerTrip,
        params CandidateYield[] yields
    ) =>
        new()
        {
            Kind = kind,
            Id = id,
            State = CandidateState.Closed,
            Yields = EquatableArray.From(yields),
            Lead = new CandidateLead.Days(0),
            Work = new CandidateWork.OneShot(),
            UpfrontCost = new CandidateCost { LaborTicks = labor },
            Path = path,
            Nearness = new SourceNearness.Unmeasured(),
            NeedsHaul = haul,
            UnitsPerTrip = unitsPerTrip,
        };
}
