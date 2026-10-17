namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>bed/*</c> acceptance cases; <see cref="Register"/> registers them.</summary>
public static class BedCases
{
    /// <summary>Registers every <c>bed/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(BedAssign.Case());
    }
}
