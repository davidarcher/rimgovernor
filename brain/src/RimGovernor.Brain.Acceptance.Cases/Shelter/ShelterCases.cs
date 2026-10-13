namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>shelter/*</c> acceptance cases (Go <c>cases/shelter</c>): the shelter's construction path, its climate-keyed
/// campfire and cooler, its retirement, the sheltering planner and the solo-cold start. <see cref="Register"/> replaces the
/// Go package's <c>init()</c>.
/// </summary>
public static class ShelterCases
{
    /// <summary>Registers every <c>shelter/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ShelterBunks.Case());
        foreach (var climate in ShelterClimate.Cases())
        {
            registry.Register(climate);
        }

        registry.Register(ShelterRetirement.Case());
        foreach (var sheltering in ShelterSheltering.Cases())
        {
            registry.Register(sheltering);
        }

        registry.Register(ShelterSiting.Case());
        registry.Register(ShelterSolo.Case());
    }
}
