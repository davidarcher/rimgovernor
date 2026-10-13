namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>research/*</c> cases (Go <c>cases/research</c>).</summary>
public static class ResearchCases
{
    /// <summary>Registers every <c>research/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ResearchReads.Case());
    }
}
