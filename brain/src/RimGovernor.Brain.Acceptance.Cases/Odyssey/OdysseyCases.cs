namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The Odyssey acceptance cases; <see cref="Register"/> registers them.</summary>
public static class OdysseyCases
{
    /// <summary>Registers every <c>odyssey/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(OdysseyLavaField.Case());
    }
}
