namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The colony review run (Go <c>cases/review</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class ReviewCases
{
    /// <summary>Registers every <c>review/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ReviewColonyWeek.Case());
    }
}
