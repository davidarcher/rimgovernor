namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>authority/*</c> acceptance cases (Go <c>cases/authority</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class AuthorityCases
{
    /// <summary>Registers every <c>authority/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(AuthorityDisconnect.Case());
        registry.Register(AuthorityWarm.Case());
    }
}
