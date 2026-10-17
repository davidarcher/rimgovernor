namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>refrigeration/*</c> acceptance cases: the food-stores/refrigeration vertical.</summary>
public static class RefrigerationCases
{
    /// <summary>Registers every <c>refrigeration/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(RefrigerationBuild.Case());
    }
}
