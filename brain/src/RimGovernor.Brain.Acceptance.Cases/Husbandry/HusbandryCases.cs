namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>husbandry/*</c> cases. <see cref="Register"/> registers them.</summary>
public static class HusbandryCases
{
    /// <summary>Registers every <c>husbandry/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(HusbandryDispatch.Case());
    }
}
