namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>authority/*</c> acceptance cases; <see cref="Register"/> registers them.</summary>
public static class AuthorityCases
{
    /// <summary>Registers every <c>authority/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(AuthorityWarm.Case());
        registry.Register(AuthorityPlayerSpeed.Case());
    }
}
