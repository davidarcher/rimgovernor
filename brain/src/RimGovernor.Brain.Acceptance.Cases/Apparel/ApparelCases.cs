namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>apparel/*</c> acceptance cases: native probes of apparel mood mechanics.</summary>
public static class ApparelCases
{
    /// <summary>Registers every <c>apparel/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(DeadMans.Case());
        registry.Register(TaintedFilter.Case());
    }
}
