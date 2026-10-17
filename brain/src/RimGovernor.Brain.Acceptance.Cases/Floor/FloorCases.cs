namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>floor/*</c> acceptance cases (Go <c>cases/floor</c>): the rooms/flooring Need vertical.</summary>
public static class FloorCases
{
    /// <summary>Registers every <c>floor/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(FloorKitchen.Case());
    }
}
