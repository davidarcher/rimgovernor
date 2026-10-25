using RimGovernor.Brain.Policy;
using P = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Domain.Tests;

internal static class PreviewTestFacts
{
    internal static PreviewSource.Native Source(Cell cell, EquatableArray<P.PlacementBlocker> blockers = default) => new(new P.PlacementEvaluated
    {
        CanPlace = blockers.Count == 0, MadeFromStuff = false, ResearchFinished = true, BuildableByPlayer = true,
        CostList = [], Materials = new() { Rows = [] }, Rotations = [new P.PlacementRotation
        {
            Accepted = blockers.Count == 0, Reason = "", OccupiedCells = [cell], InteractionCells = [], BlockingThings = blockers,
            WatchCells = P.WatchAccess.NotWatched, Wind = new BuildingWind.NotWindTurbine(),
        }],
    }, 0);
}
