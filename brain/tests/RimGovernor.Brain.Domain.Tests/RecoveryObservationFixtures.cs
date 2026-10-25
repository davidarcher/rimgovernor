using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

internal static class RecoveryObservationFixtures
{
    internal static O.RecoveryRestriction Restriction(string pawn, string? area = null) =>
        new() { Pawn = new() { Id = pawn }, AreaId = area };
}
