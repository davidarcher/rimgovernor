namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The need acceptance cases (Go <c>cases/needs</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class NeedsCases
{
    /// <summary>Registers every <c>needs/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(NeedsFreeze.Case());
    }
}
