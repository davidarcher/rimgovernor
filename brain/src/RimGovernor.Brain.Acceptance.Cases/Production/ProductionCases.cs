namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>production/*</c> cases. <see cref="Register"/> registers them.</summary>
public static class ProductionCases
{
    /// <summary>Registers every <c>production/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ProductionApparelPolicy.Case());
        foreach (var buriedSteel in ProductionBuriedSteel.Cases())
        {
            registry.Register(buriedSteel);
        }

        registry.Register(ProductionLadder.Case());
    }
}
