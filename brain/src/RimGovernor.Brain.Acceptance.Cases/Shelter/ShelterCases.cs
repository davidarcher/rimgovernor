namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>shelter/*</c> acceptance cases: the shelter's construction path, its retired ground's clearance and the
/// sheltering from a real threat. <see cref="Register"/> registers them.
/// </summary>
public static class ShelterCases
{
    /// <summary>Registers every <c>shelter/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ShelterBunks.Case());
        registry.Register(ShelterRetirement.Case());
        foreach (var sheltering in ShelterSheltering.Cases())
        {
            registry.Register(sheltering);
        }
    }
}