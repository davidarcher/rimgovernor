namespace RimGovernor.Brain.Policy;

/// <summary>
/// A reference to an existing owner predicate, not an independent stock target or skill floor. The owner is a
/// NeedId under its <see cref="NeedId.FilingKey"/> (#3250).
/// </summary>
public sealed record ServiceTarget(FilingKey Owner, string Predicate)
{
    /// <summary>The target of a Need's own predicate.</summary>
    public static ServiceTarget ForNeed(NeedId need, string predicate = "need_satisfied")
    {
        need.Validate();
        return new(need.FilingKey, predicate);
    }

}
/// <summary>Existing decoded owners; no duplicate observations or persisted goals.</summary>
public sealed record EssentialServiceInputs
{
    public EquatableArray<ProductionBench> Benches { get; init; }
    public PowerTopology Power { get; init; } = new();
}
