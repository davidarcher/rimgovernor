namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>route/*</c> cases.</summary>
public static class RouteCases
{
    /// <summary>Registers every <c>route/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(RouteStockpile.Case());
    }
}
