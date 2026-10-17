namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>movement/*</c> acceptance cases.</summary>
public static class MovementCases
{
    /// <summary>Registers every <c>movement/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(MovementArrival.Case());
    }
}
