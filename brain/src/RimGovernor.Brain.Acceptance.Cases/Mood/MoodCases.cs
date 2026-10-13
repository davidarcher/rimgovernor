namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>mood/*</c> acceptance cases (Go <c>cases/mood</c>): arrest and subdue custody, the gathering, headroom and
/// ledger mood policies and joy relief. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class MoodCases
{
    /// <summary>Registers every <c>mood/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var arrest in Arrest.All())
        {
            registry.Register(arrest);
        }

        registry.Register(Gathering.Case());
        registry.Register(Headroom.Case());
        registry.Register(Ledger.Case());
        registry.Register(Relief.Case());
        registry.Register(Subdue.Case());
    }
}
