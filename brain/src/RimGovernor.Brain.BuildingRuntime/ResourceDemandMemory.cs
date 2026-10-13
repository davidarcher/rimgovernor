namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The resource demand the latest review of one world derived (<c>ResourceDemandOf</c>, as the detectors read
/// it): derived state the Rounder keeps in memory, empty until the first review after a restart. Every planner
/// and the work coverage read it.
/// </summary>
internal sealed class ResourceDemandMemory
{
    private readonly object _mu = new();
    private GenerationSnapshot _snapshot;
    private DerivedDemand _demand = new();

    public void Set(GenerationSnapshot snapshot, DerivedDemand demand)
    {
        lock (_mu)
        {
            _snapshot = snapshot;
            _demand = demand;
        }
    }

    /// <summary>The demand the review of <paramref name="snapshot"/>'s world derived; none for another world.</summary>
    public DerivedDemand Get(GenerationSnapshot snapshot)
    {
        lock (_mu)
        {
            return _snapshot != snapshot ? new DerivedDemand() : _demand;
        }
    }
}

public sealed partial class Rounder
{
    /// <summary>The stock targets every resource planner dispatches on: the review's demand.</summary>
    internal IReadOnlyDictionary<Resource, long> ResourceTargets(GenerationSnapshot snapshot) => _demand.Get(snapshot).Needs;

    /// <summary>
    /// The stock the station chain sizes the colony's rooms for: the review's demand, each resource of the
    /// acceptance fault <c>Faults.StockDemand</c> raised to at least its count. It reaches the layout only; no
    /// planner acquires it.
    /// </summary>
    internal IReadOnlyDictionary<Resource, long> StationNeeds(GenerationSnapshot snapshot)
    {
        var needs = _demand.Get(snapshot).Needs;
        if (_stockDemand.Count == 0)
        {
            return needs;
        }
        var result = new Dictionary<Resource, long>(needs);
        foreach (var (resource, count) in _stockDemand)
        {
            result[resource] = Math.Max(result.GetValueOrDefault(resource), count);
        }
        return result;
    }
}
