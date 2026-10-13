namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>bills/*</c> acceptance cases (Go <c>cases/bills</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class BillsCases
{
    /// <summary>Registers every <c>bills/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(BillsCensus.Case());
    }
}
