namespace RimGovernor.Brain.Policy;

/// <summary>First-plan derivation runs after the initial review, so its absence is a lifecycle state rather than missing native geometry.</summary>
public abstract record StorageLayout
{
    private StorageLayout() { }
    public sealed record AwaitingFirstPlan : StorageLayout;
    public sealed record Planned(EquatableArray<Cell> Walkways) : StorageLayout;
}
