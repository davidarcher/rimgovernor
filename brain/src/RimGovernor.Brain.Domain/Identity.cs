namespace RimGovernor.Brain.Domain;

/// <summary>
/// Scopes in-flight work to one loaded world (colony, map, load token), the plan revision it serves and
/// the native order generation it was issued under. There is one author of orders, so no per-acquisition
/// direction counter exists: a reload changes Load, and native order-history drift changes Native.
/// </summary>
public readonly record struct GenerationSnapshot(
    ColonyId Colony,
    MapId Map,
    LoadId Load,
    PlanId Plan,
    PlanRevision Revision,
    NativeGeneration Native
)
{
    public bool IsValid => !(!Colony.IsValid || !Load.IsValid || !Plan.IsValid || Map.Value < 0);

    public void Validate() => DomainException.Require(IsValid, "invalid colony, map, load or plan identity");

    public bool Matches(GenerationSnapshot other) => this == other;

    /// <summary>Whether both snapshots name one loaded map, whatever plan, revision or native generation each carries.</summary>
    public bool SameWorld(GenerationSnapshot other) => Colony == other.Colony && Map == other.Map && Load == other.Load;

    /// <summary>The goal world check: goals belong to the save, so a load change keeps them.</summary>
    internal bool SameColonyMap(GenerationSnapshot other) => Colony == other.Colony && Map == other.Map;
}

public static class PlanIds
{
    /// <summary>
    /// Mints a fresh plan id at admission: a bare random version-4 UUID. Nothing re-derives it; a plan is
    /// found again by its stored (goal, epoch, method) key. (Go <c>MintPlanID</c>.)
    /// </summary>
    public static PlanId MintPlanId() => new(Guid.NewGuid().ToString("D"));
}
