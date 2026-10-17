namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Support cleaning around a bill (<c>clean/bench</c>) and a patient's bed (<c>clean/care</c>,
/// <c>clean/care-routine</c>).
/// </summary>
public static class CleanCases
{
    /// <summary>Registers every <c>clean/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var c in CleanSupport.Cases())
        {
            registry.Register(c);
        }
    }
}
