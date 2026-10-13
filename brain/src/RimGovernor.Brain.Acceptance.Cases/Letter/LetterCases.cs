namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The letter acceptance cases (Go <c>cases/letter</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class LetterCases
{
    /// <summary>Registers every <c>letter/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LetterPause.Case());
    }
}
