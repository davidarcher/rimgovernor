namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>solo/*</c> cases (Go <c>cases/solo</c>).</summary>
public static class SoloCases
{
    /// <summary>Registers every <c>solo/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(SoloSeaIce.StockCase());
        registry.Register(SoloSeaIce.RhadamantCase());
    }
}
