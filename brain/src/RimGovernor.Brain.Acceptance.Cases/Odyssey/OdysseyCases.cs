namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The Odyssey acceptance cases (Go <c>cases/odyssey</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class OdysseyCases
{
    /// <summary>Registers every <c>odyssey/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(OdysseyLavaField.Case());
    }
}
