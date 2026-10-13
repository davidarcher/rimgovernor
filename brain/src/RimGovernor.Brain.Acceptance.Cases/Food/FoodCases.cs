namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>food/*</c> acceptance cases (Go <c>cases/food</c>): isolated nutrition channels verified against native
/// facts. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class FoodCases
{
    /// <summary>Registers every <c>food/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(FoodFixture.Case());
        registry.Register(FoodFishing.Case());
        registry.Register(FoodHuntChainRule.Case());
        registry.Register(FoodLedger.Case());
        registry.Register(FoodLedgerHunt.Case());
        registry.Register(FoodReserve.Case());
        registry.Register(FoodStarving.Case());
        registry.Register(FoodStarvingRecovery.Case());
    }
}
