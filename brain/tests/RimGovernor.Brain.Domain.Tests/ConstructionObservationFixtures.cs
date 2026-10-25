using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;
using P = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Domain.Tests;

internal static class ConstructionObservationFixtures
{
    internal static O.BuildingState Row(Building building, string id, EquatableArray<Cell> cells = default)
    {
        var occupied = cells.Count == 0 ? new[] { building.Cell } : cells.ToArray();
        return PowerBuildingFixtures.Trader(id, building.Definition, building.Cell, 0, 0, false) with
        {
            Rotation = building.Rotation switch
            {
                Rotation.North => P.Rotation.North,
                Rotation.East => P.Rotation.East,
                Rotation.South => P.Rotation.South,
                Rotation.West => P.Rotation.West,
                _ => throw new ArgumentOutOfRangeException(nameof(building)),
            },
            Stuff = string.IsNullOrEmpty(building.Stuff) ? null : building.Stuff,
            Service = BuildingServiceWire.NoService,
            Occupied = new O.Rectangle
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
        O.FinishingMinimum? minimum = null, O.ConstructionGate? gate = null) => new()
        {
            Observed = Row(building, id) with
            {
                Status = stage == ConstructionSite.BlueprintStage ? O.BuildingStatus.Blueprint : O.BuildingStatus.Frame,
                Construction = new O.ConstructionState.Build
                {
                    Definition = building.Definition,
                    Resources = [new O.MaterialDeficit { DefName = string.IsNullOrEmpty(building.Stuff) ? "WoodLog" : building.Stuff, StillNeeded = resourcesComplete ? 0 : 1 }],
                    QualitySensitive = qualitySensitive,
                    NativeFinishingSkill = nativeFinishingSkill,
                    Minimum = minimum,
                    Gate = gate ?? new O.ConstructionGate.Ungated(),
                },
            },
        };
}
