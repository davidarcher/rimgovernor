namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>sleeping/*</c> cases.</summary>
public static class SleepingCases
{
    /// <summary>Registers every <c>sleeping/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(SleepingBedroomWaitsForValue.Case());
    }
}
