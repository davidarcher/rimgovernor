namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>child/*</c> acceptance cases (Go <c>cases/child</c>); <see cref="Register"/> replaces the Go package's
/// <c>init()</c>.
/// </summary>
public static class ChildCases
{
    /// <summary>Registers every <c>child/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ChildFirstChild.Case());
        registry.Register(ChildMechGestation.Case());
    }
}
