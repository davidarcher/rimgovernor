namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>animals/*</c> cases (Go <c>cases/animals</c>). <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class AnimalsCases
{
    /// <summary>Registers every <c>animals/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(AnimalsContainment.Case());
    }
}
