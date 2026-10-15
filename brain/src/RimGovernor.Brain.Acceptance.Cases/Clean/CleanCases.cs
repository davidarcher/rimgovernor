namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Normal selected support cleaning around a
/// bill (<c>clean/bench</c>) and a patient's bed (<c>clean/care</c>).
/// </summary>
public static class CleanCases
{
    /// <summary>Registers every <c>clean/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CleanBench.Case());
        registry.Register(CleanCare.Case());
        registry.Register(CleanCare.RoutineCase());
    }
}
