namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The draft acceptance cases.</summary>
public static class DraftCases
{
    /// <summary>Registers <c>draft/intent</c>.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(DraftIntent.Case());
    }
}
