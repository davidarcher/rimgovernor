namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>animals/*</c> cases. <see cref="Register"/> registers them.</summary>
public static class AnimalsCases
{
    /// <summary>Registers every <c>animals/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(AnimalsContainment.Case());
    }
}
