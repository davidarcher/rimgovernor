namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The need acceptance cases; <see cref="Register"/> registers them.</summary>
public static class NeedsCases
{
    /// <summary>Registers every <c>needs/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(NeedsFreeze.Case());
        registry.Register(NeedsRecreationGain.Case());
        registry.Register(NeedsFarMealStandby.Case());
    }
}
