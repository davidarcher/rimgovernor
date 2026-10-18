namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>food/*</c> acceptance cases: isolated nutrition channels verified against native
/// facts. <see cref="Register"/> registers them.
/// </summary>
public static class FoodCases
{
    /// <summary>Registers every <c>food/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(FoodLedger.Case());
        registry.Register(FoodLedgerHunt.Case());
        registry.Register(FoodHuntRoster.Case());
        registry.Register(FoodReserve.Case());
        registry.Register(FoodStarvingRecovery.Case());
    }
}
