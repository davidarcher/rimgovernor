namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The native opportunistic-cleaning cases (Go <c>cases/clean</c>): the Harmony patches that make a pawn clean before a
/// bill (<c>clean/bench</c>) and around a patient's bed (<c>clean/care</c>).
/// </summary>
public static class CleanCases
{
    /// <summary>Registers every <c>clean/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CleanBench.Case());
        registry.Register(CleanCare.Case());
    }
}
