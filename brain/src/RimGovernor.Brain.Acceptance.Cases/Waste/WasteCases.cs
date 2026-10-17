namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The waste case family.</summary>
public static class WasteCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(WasteEquality.Case());
        registry.Register(WasteBurnable.Case());
    }
}
