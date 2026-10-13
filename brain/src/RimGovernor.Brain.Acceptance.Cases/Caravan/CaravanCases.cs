namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The caravan acceptance cases (Go <c>cases/caravan</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class CaravanCases
{
    /// <summary>Registers every <c>caravan/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CaravanDeparture.Case());
    }
}
