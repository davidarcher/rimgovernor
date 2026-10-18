namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The Ideology acceptance cases; <see cref="Register"/> registers them.</summary>
public static class IdeologyCases
{
    /// <summary>Registers every <c>ideology/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(IdeologyConvert.Case());
        registry.Register(IdeologyReform.Case());
        registry.Register(IdeologyRitual.Case());
    }
}
