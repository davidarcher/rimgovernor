namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>mood/*</c> acceptance cases: arrest and subdue custody and the native gathering. <see cref="Register"/> registers them.
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
        registry.Register(Subdue.Case());
    }
}