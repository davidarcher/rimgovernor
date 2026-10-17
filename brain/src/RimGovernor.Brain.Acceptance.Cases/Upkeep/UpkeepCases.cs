namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The upkeep case family.</summary>
public static class UpkeepCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(UpkeepAutoHomeArea.Case());
        registry.Register(UpkeepCouple.Case());
        registry.Register(UpkeepHomeCoverage.Case());
    }
}
