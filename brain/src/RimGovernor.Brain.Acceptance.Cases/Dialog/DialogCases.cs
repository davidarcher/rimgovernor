namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>dialog/*</c> acceptance cases (Go <c>cases/dialog</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class DialogCases
{
    /// <summary>Registers every <c>dialog/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(DialogPause.Case());
    }
}
