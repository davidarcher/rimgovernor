namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// A value the latest review of one world derived: derived state the Rounder keeps in memory, <c>empty</c> until the first
/// review after a restart and for another world. The resource demand (<c>ResourceDemandOf</c>, as the detectors read it;
/// every planner and the work coverage read it) and the per-pawn gear-filter outcome (GearFilterChoices, #3285; the
/// military read stamps it onto GearPawn.AllowTainted) are two.
/// </summary>
internal sealed class ReviewMemory<T>(T empty)
{
    private readonly object _mu = new();
    private readonly T _empty = empty;
    private GenerationSnapshot _snapshot;
    private T _value = empty;

    public void Set(GenerationSnapshot snapshot, T value)
    {
        lock (_mu)
        {
            _snapshot = snapshot;
            _value = value;
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

public sealed partial class Rounder
{
    /// <summary>The latest review's trade measures, read by the economy family's trade and caravan Needs (#3316).</summary>
    internal TradeReviewMemory TradeMemory { get; } = new();

    /// <summary>The stock targets every resource planner dispatches on: the review's demand.</summary>
    internal IReadOnlyDictionary<Resource, long> ResourceTargets(GenerationSnapshot snapshot) => Demand.Get(snapshot).Needs;

    /// <summary>
    /// The stock the station chain sizes the colony's rooms for: the review's demand, each resource of the
    /// acceptance fault <c>Faults.StockDemand</c> raised to at least its count. It reaches the layout only; no
    /// planner acquires it.
    /// </summary>
    internal IReadOnlyDictionary<Resource, long> StationNeeds(GenerationSnapshot snapshot)
    {
        var needs = Demand.Get(snapshot).Needs;
        if (StockDemand is not { Count: > 0 } stockDemand)
        {
            return needs;
        }
        var result = new Dictionary<Resource, long>(needs);
        foreach (var (resource, count) in stockDemand)
        {
            result[resource] = Math.Max(result.GetValueOrDefault(resource), count);
        }
        return result;
    }
}
