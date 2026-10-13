namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>farm/*</c> cases (Go <c>cases/farm</c>). <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class FarmCases
{
    /// <summary>Registers every <c>farm/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(FarmBlight.Case());
        registry.Register(FarmCalendar.Case());
        registry.Register(FarmLattice.Case());
        registry.Register(FarmPlantation.Case());
        registry.Register(FarmSelectHydroponics.Case());
        foreach (var nonFood in FarmNonFood.Cases())
        {
            registry.Register(nonFood);
        }
    }
}
