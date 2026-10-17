namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>child/*</c> acceptance cases; <see cref="Register"/> registers them.
/// </summary>
public static class ChildCases
{
    /// <summary>Registers every <c>child/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ChildFirstChild.Case());
    }
}
