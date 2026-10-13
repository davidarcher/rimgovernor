namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The Ideology acceptance cases (Go <c>cases/ideology</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class IdeologyCases
{
    /// <summary>Registers every <c>ideology/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(IdeologyReform.Case());
        registry.Register(IdeologyRitual.Case());
    }
}
