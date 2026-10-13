namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>supplies/*</c> cases (Go <c>cases/supplies</c>).</summary>
public static class SuppliesCases
{
    /// <summary>Registers every <c>supplies/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(SuppliesReads.Case());
    }
}
