using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;
using C = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The construction census read (kept from the deleted ForecastConstruction tests, #3894).</summary>
public sealed class ConstructionCensusTests
{
    private static readonly Resource Material = new("TestMaterial");

    [Fact]
    public void NativeConstructionProjectionRetainsRemainingWorkAndOwedMaterials()
    {
        var row = new O.BuildingState
        {
            Building = new O.EntityRef { Id = "frame", DefName = "Frame_Wall", Label = "wall (building)", Place = new O.EntityPlace.OnMap(0, new C.Cell { X = 1, Z = 1 }) },
            Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North,
            Stuff = null,
            Faction = null,
            Status = O.BuildingStatus.Frame,
            HitPoints = new O.BuildingHitPoints.Untracked(),
            Construction = new O.ConstructionState.Build
            {
                Definition = "TestBuilding",
                WorkLeft = 123,
                Resources = [new O.MaterialDeficit { DefName = Material.Value, StillNeeded = 7 }],
                QualitySensitive = false,
                NativeFinishingSkill = 0,
                Minimum = null,
                Gate = new O.ConstructionGate.Ungated(),
            },
            Service = BuildingServiceWire.NoService,
            Settings = new O.BuildingSettings { Forbidden = false, Control = new O.BuildingControl.None() },
            Burning = false,
            Occupied = new O.Rectangle { Minimum = new C.Cell { X = 1, Z = 1 }, Maximum = new C.Cell { X = 1, Z = 1 } },
            Anomaly = null,
            Door = null,
        };
        var observed = ConstructionReads.ConstructionFromCensus(new(null, Buildings.Of(row)));
        Assert.Equal("TestBuilding", observed.Sites[0].Building!.Definition);
        Assert.Equal(123, observed.Sites[0].WorkLeft);
        Assert.Equal(7, observed.Sites[0].MaterialsOwed[0].Count);
        Assert.False(observed.Sites[0].ResourcesComplete);
        Assert.Null(observed.Sites[0].Minimum);
        Assert.IsType<O.ConstructionGate.Ungated>(observed.Sites[0].Gate);
        var gated = row with
        {
            Construction = ((O.ConstructionState.Build)row.Construction) with
            {
                Minimum = new O.FinishingMinimum { Skill = 6, EligibleFinishers = 2 },
                Gate = new O.ConstructionGate.Tiered { Tier = ConstructionTier.Sustain },
            },
        };
        var gatedSite = ConstructionReads.ConstructionFromCensus(new(null, Buildings.Of(gated))).Sites[0];
        Assert.Equal(new O.FinishingMinimum { Skill = 6, EligibleFinishers = 2 }, gatedSite.Minimum);
        Assert.Equal(ConstructionTier.Sustain, Assert.IsType<O.ConstructionGate.Tiered>(gatedSite.Gate).Tier);
        var missing = row with { Construction = null };
        Assert.Throws<ObservationContractException>(() => ConstructionReads.ConstructionFromCensus(new(null, Buildings.Of(missing))));
    }
}
