namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The presentation acceptance cases (Go <c>cases/presentation</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class PresentationCases
{
    /// <summary>Registers every <c>presentation/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PresentationRoster.Case());
    }
}
