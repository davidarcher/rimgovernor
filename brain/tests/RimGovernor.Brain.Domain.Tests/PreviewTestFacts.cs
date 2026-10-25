using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

internal static class PreviewTestFacts
{
    internal static PreviewSource.Native Source(Cell cell, EquatableArray<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker> blockers = default) => new(new RimGovernor.Brain.GameFacts.Placement.PlacementEvaluated
    {
        CanPlace = blockers.Count == 0, MadeFromStuff = false, ResearchFinished = true, BuildableByPlayer = true,
        CostList = [], Materials = new() { Rows = [] }, Rotations = [new RimGovernor.Brain.GameFacts.Placement.PlacementRotation
        {
            Accepted = blockers.Count == 0, Reason = "", OccupiedCells = [cell], InteractionCells = [], BlockingThings = blockers,
            WatchCells = RimGovernor.Brain.GameFacts.Placement.WatchAccess.NotWatched, Wind = new BuildingWind.NotWindTurbine(),
        }],
    }, 0);
}
