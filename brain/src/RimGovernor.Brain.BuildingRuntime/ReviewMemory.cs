using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// A value the latest review of one world derived: derived state the Rounder keeps in memory, <c>empty</c> until the first
/// review after a restart and for another world. The resource demand (<c>ResourceDemandOf</c>, as the detectors read it;
/// every planner and the work coverage read it) and the per-pawn gear outcome (GearChoices, #3285, #3329;
/// ObserveRoundsAsync stamps it onto GearPawn.AllowTainted, ColdFloor and HeatFloor) are two.
/// </summary>
internal sealed class ReviewMemory<T>(T empty)
{
    private readonly object _mu = new();
    private readonly T _empty = empty;
    private GenerationSnapshot _snapshot;
    private bool _set;
    private T _value = empty;

    public void Set(GenerationSnapshot snapshot, T value)
    {
        lock (_mu)
        {
            _snapshot = snapshot;
            _value = value;
            _set = true;
        }
    }

    /// <summary>The value the review of <paramref name="snapshot"/>'s world derived; unknown before one did.</summary>
    public Fact<T> Read(GenerationSnapshot snapshot)
    {
        lock (_mu)
        {
            return _set && _snapshot == snapshot ? Known(_value) : Unknown<T>();
        }
    }

    /// <summary>The value the review of <paramref name="snapshot"/>'s world derived; empty for another world.</summary>
    public T Get(GenerationSnapshot snapshot)
    {
        lock (_mu)
        {
            return _snapshot != snapshot ? _empty : _value;
        }
    }

    /// <summary>Applies <paramref name="change"/> to <paramref name="snapshot"/>'s value (empty for another world) and keeps the result for that world.</summary>
    public void Update(GenerationSnapshot snapshot, Func<T, T> change)
    {
        lock (_mu)
        {
            _value = change(_snapshot != snapshot || !_set ? _empty : _value);
            _snapshot = snapshot;
            _set = true;
        }
    }
}

/// <summary>
/// The trade review the latest review of one world measured (#3316): the silver gap and whether the caravans on the
/// map are traded out. The economy family's trade and caravan Needs read it; unknown for another world or before the
/// first review after a restart.
/// </summary>
internal sealed class TradeReviewMemory
{
    private readonly object _mu = new();
    private GenerationSnapshot _snapshot;
    private TradeReview _review = new();

    public void Set(GenerationSnapshot snapshot, TradeReview review)
    {
        lock (_mu)
        {
            _snapshot = snapshot;
            _review = review;
        }
    }

    /// <summary>The trade review of <paramref name="snapshot"/>'s world; all unknown for another world.</summary>
    public TradeReview Get(GenerationSnapshot snapshot)
    {
        lock (_mu)
        {
            return _snapshot != snapshot ? new TradeReview() : _review;
        }
    }
}

/// <summary>What the <c>layout/defense</c> Need's step last verified of the stored layout (#3008).</summary>
internal sealed record DefenseVerified
{
    /// <summary>The step found every tier admitted and none owed, at <see cref="Tick"/>.</summary>
    public bool Complete { get; init; }

    /// <summary>When the step last verified the layout or spent a tier's refusal budget: the re-verify window starts here.</summary>
    public Tick Tick { get; init; }

    /// <summary>The map's latest fight plan at <see cref="Tick"/>; a newer fight makes the layout re-verify.</summary>
    public string Combat { get; init; } = "";

    /// <summary>When the step last probed the turret and mortar siting or a turret replacement; the default probes at once.</summary>
    public Tick TurretsProbed { get; init; }
}

/// <summary>
/// The defense layout's verification per world (#3008): derived state the Rounder keeps in memory, empty after a restart
/// and for another world (colony, load, map), so the stored layout re-verifies and its siting is probed again.
/// </summary>
internal sealed class DefenseVerification
{
    private readonly object _mu = new();
    private StoreWorld _world;
    private DefenseVerified _value = new();

    public DefenseVerified Get(StoreWorld world)
    {
        lock (_mu)
        {
            return _world != world ? new DefenseVerified() : _value;
        }
    }

    public void Set(StoreWorld world, DefenseVerified value)
    {
        lock (_mu)
        {
            _world = world;
            _value = value;
        }
    }

    /// <summary>Applies <paramref name="change"/> to the world's verification.</summary>
    public void Update(StoreWorld world, Func<DefenseVerified, DefenseVerified> change)
    {
        lock (_mu)
        {
            _value = change(_world != world ? new DefenseVerified() : _value);
            _world = world;
        }
    }
}

public sealed partial class Rounder
{
    /// <summary>The defense layout's in-memory verification (#3008), read and written by the <c>layout/defense</c> Need.</summary>
    internal DefenseVerification DefenseVerification { get; } = new();

    /// <summary>The latest review's trade measures, read by the economy family's trade and caravan Needs (#3316).</summary>
    internal TradeReviewMemory TradeMemory { get; } = new();

    /// <summary>Whether the latest review owed work assignments (WorkAssignmentsOwed), read by the admin/work-assignments Need (#3304); unknown for another world.</summary>
    internal ReviewMemory<Fact<bool>> WorkAssignments { get; } = new(Unknown<bool>());

    /// <summary>
    /// Keeps a review's per-pawn gear outcome (filter and floor) when its comparator ran (#3325). A review with no coupled
    /// selection compared nothing, so the last outcome stands until a review with one replaces it.
    /// </summary>
    internal static void NoteGearChoices(ReviewMemory<EquatableArray<GearChoice>> memory, GenerationSnapshot snapshot, ForecastCoupledCandidates? candidates, ForecastCoupledSelection? selection)
    {
        if (selection is not null)
        {
            memory.Set(snapshot, EffectSourcesRules.GearChoices(candidates, selection));
        }
    }

    /// <summary>The stock targets every resource planner dispatches on: the review's demand.</summary>
    internal IReadOnlyDictionary<Resource, long> ResourceTargets(GenerationSnapshot snapshot) => Demand.Get(snapshot).Needs;

    /// <summary>
    /// The stock the station chain sizes the colony's rooms for: the review's demand, each resource of the
    /// acceptance fault <c>Faults.StockDemand</c> raised to at least its count. It reaches the layout only; no
    /// planner acquires it. Unknown until a review of this world derived the demand; an empty one is known.
    /// </summary>
    internal Fact<IReadOnlyDictionary<Resource, long>> StationNeeds(GenerationSnapshot snapshot)
    {
        if (!Demand.Read(snapshot).TryGetValue(out var demand))
        {
            return Unknown<IReadOnlyDictionary<Resource, long>>();
        }
        var needs = demand.Needs;
        if (StockDemand is not { Count: > 0 } stockDemand)
        {
            return Known(needs);
        }
        var result = new Dictionary<Resource, long>(needs);
        foreach (var (resource, count) in stockDemand)
        {
            result[resource] = Math.Max(result.GetValueOrDefault(resource), count);
        }
        return Known<IReadOnlyDictionary<Resource, long>>(result);
    }
}
