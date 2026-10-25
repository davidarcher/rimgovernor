using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

internal static class ConstructionObservationFixtures
{
    internal static RimGovernor.Brain.GameFacts.Observations.BuildingState Row(Building building, string id, EquatableArray<Cell> cells = default)
    {
        var occupied = cells.Count == 0 ? new[] { building.Cell } : cells.ToArray();
        return PowerBuildingFixtures.Trader(id, building.Definition, building.Cell, 0, 0, false) with
        {
            Rotation = building.Rotation switch
            {
                Rotation.North => RimGovernor.Brain.GameFacts.Placement.Rotation.North,
                Rotation.East => RimGovernor.Brain.GameFacts.Placement.Rotation.East,
                Rotation.South => RimGovernor.Brain.GameFacts.Placement.Rotation.South,
                Rotation.West => RimGovernor.Brain.GameFacts.Placement.Rotation.West,
                _ => throw new ArgumentOutOfRangeException(nameof(building)),
            },
            Stuff = string.IsNullOrEmpty(building.Stuff) ? null : building.Stuff,
            Service = BuildingServiceWire.NoService,
            Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle
            {
                Minimum = new Cell(occupied.Min(c => c.X), occupied.Min(c => c.Z)),
                Maximum = new Cell(occupied.Max(c => c.X), occupied.Max(c => c.Z)),
            },
        };
    }

    internal static CurrentBuilding Built(Building building, string id = "built", EquatableArray<Cell> cells = default) =>
        new() { Observed = Row(building, id, cells) };

    internal static ConstructionSite Site(Building building, string id = "site", string stage = ConstructionSite.BlueprintStage,
        bool resourcesComplete = false, bool qualitySensitive = false, int nativeFinishingSkill = 0,
        RimGovernor.Brain.GameFacts.Observations.FinishingMinimum? minimum = null, RimGovernor.Brain.GameFacts.Observations.ConstructionGate? gate = null) => new()
        {
            Observed = Row(building, id) with
            {
                Status = stage == ConstructionSite.BlueprintStage ? RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint : RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Frame,
                Construction = new RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build
                {
                    Definition = building.Definition,
                    Resources = [new RimGovernor.Brain.GameFacts.Observations.MaterialDeficit { DefName = string.IsNullOrEmpty(building.Stuff) ? "WoodLog" : building.Stuff, StillNeeded = resourcesComplete ? 0 : 1 }],
                    QualitySensitive = qualitySensitive,
                    NativeFinishingSkill = nativeFinishingSkill,
                    Minimum = minimum,
                    Gate = gate ?? new RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Ungated(),
                },
            },
        };
}
