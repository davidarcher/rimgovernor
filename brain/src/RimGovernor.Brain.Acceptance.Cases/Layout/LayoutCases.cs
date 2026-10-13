namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>layout/*</c> acceptance cases (Go <c>cases/layout</c>): the tiered colony layout cases staged on the layout grid
/// fixture hut (<c>test/layout_grid_prepare</c>). <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class LayoutCases
{
    /// <summary>Registers every <c>layout/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LayoutPowerSites.Case());
        registry.Register(LayoutRichSoil.Case());
        registry.Register(LayoutRing.Case());
        registry.Register(LayoutSeaIceEnvelope.Case());
        registry.Register(LayoutStorageSuccessor.Case());
    }
}
