using RimGovernor.Brain.GameFacts.Lifecycle;
using RimGovernor.Brain.GameOrders.Lifecycle;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A whole new-colony spec for tests that need one on the wire: every base member set, no scenario parts.</summary>
internal static class NewColonyFixtures
{
    internal static NewColonySpec Spec() => new()
    {
        ColonistCount = 3,
        FlatTile = false,
        MinTemperature = new TemperatureLimit.OpenCase(),
        MaxTemperature = new TemperatureLimit.OpenCase(),
        MapSize = 250,
        PlanetCoverage = 0.3f,
        GovernorIdeoligion = false,
        PlayerFaction = ScenarioPlayerFaction.Base,
        ArrivalMethod = ScenarioArrivalMethod.Base,
    };
}
