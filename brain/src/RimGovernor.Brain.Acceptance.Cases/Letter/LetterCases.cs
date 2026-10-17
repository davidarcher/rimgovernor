namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>letter/*</c> acceptance cases.</summary>
public static class LetterCases
{
    /// <summary>Registers every <c>letter/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LetterNotifications.Case());
    }
}
