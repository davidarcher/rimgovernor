
namespace RimGovernor.Brain.Domain.Tests;

internal static class RecoveryObservationFixtures
{
    internal static RimGovernor.Brain.GameFacts.Observations.RecoveryRestriction Restriction(string pawn, string? area = null) =>
        new() { Pawn = new() { Id = pawn }, AreaId = area };
}
