namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>goals/*</c> cases: the player's goals steer the served brain (#3478).</summary>
public static class GoalsCases
{
    /// <summary>Registers every <c>goals/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(GoalsResearch.Case());
        registry.Register(GoalsThemeReform.Case());
    }
}
