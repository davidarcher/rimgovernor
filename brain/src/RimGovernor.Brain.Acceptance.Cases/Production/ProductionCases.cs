namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>production/*</c> cases (Go <c>cases/production</c>). <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class ProductionCases
{
    /// <summary>Registers every <c>production/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ProductionApparelPolicy.Case());
    }
}
