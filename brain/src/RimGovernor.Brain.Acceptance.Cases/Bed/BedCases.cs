namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>bed/*</c> acceptance cases (Go <c>cases/bed</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class BedCases
{
    /// <summary>Registers every <c>bed/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(BedAssign.Case());
    }
}
