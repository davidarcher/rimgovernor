using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The construction census read (kept from the deleted ForecastConstruction tests, #3894).</summary>
public sealed class ConstructionCensusTests
{
    private static readonly Resource Material = new("TestMaterial");

    [Fact]
    public void NativeConstructionProjectionRetainsOwedMaterialsGateAndMinimum()
    {
        var row = new RimGovernor.Brain.GameFacts.Observations.BuildingState
        {
            Roofed = false, RainVulnerable = false, FuelDefinitions = [], Turret = null, Conduit = RimGovernor.Brain.GameFacts.Observations.ConduitKind.None,
            Building = new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id = "frame", DefName = "Frame_Wall", Label = "wall (building)", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = 1, Z = 1 }) },
            Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.North,
            Stuff = null,
            Faction = null,
            Status = RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Frame,
            HitPoints = new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Untracked(),
            Construction = new RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build
            {
                Definition = "TestBuilding",
                Resources = [new RimGovernor.Brain.GameFacts.Observations.MaterialDeficit { DefName = Material.Value, StillNeeded = 7 }],
                QualitySensitive = false,
                NativeFinishingSkill = 0,
                Minimum = null,
                Gate = new RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Ungated(),
            },
            Service = BuildingServiceWire.NoService,
            Settings = new RimGovernor.Brain.GameFacts.Observations.BuildingSettings { Forbidden = false, Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None() },
            Burning = false,
            Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = new RimGovernor.Brain.Domain.Cell { X = 1, Z = 1 }, Maximum = new RimGovernor.Brain.Domain.Cell { X = 1, Z = 1 } },
            Anomaly = null,
            Door = null,
        };
        var observed = ConstructionReads.ConstructionFromTables(new(Buildings.Of(row), Pawns.Of(), Things.Of(), []));
        Assert.Same(row, observed.Sites[0].Observed);
        Assert.Same(row.Construction, observed.Sites[0].Construction);
        Assert.Equal("TestBuilding", observed.Sites[0].Building!.Definition);
        Assert.Equal(7, observed.Deficit[Material]);
        Assert.False(observed.Sites[0].ResourcesComplete);
        Assert.Null(observed.Sites[0].Minimum);
        Assert.IsType<RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Ungated>(observed.Sites[0].Gate);
        var gated = row with
        {
            Construction = ((RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build)row.Construction) with
            {
                Minimum = new RimGovernor.Brain.GameFacts.Observations.FinishingMinimum { Skill = 6, EligibleFinishers = 2 },
                Gate = new RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Tiered { Tier = ConstructionTier.Sustain },
            },
        };
        var gatedSite = ConstructionReads.ConstructionFromTables(new(Buildings.Of(gated), Pawns.Of(), Things.Of(), [])).Sites[0];
        Assert.Same(gated, gatedSite.Observed);
        Assert.Same(((RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build)gated.Construction).Minimum, gatedSite.Minimum);
        Assert.Equal(new RimGovernor.Brain.GameFacts.Observations.FinishingMinimum { Skill = 6, EligibleFinishers = 2 }, gatedSite.Minimum);
        Assert.Equal(ConstructionTier.Sustain, Assert.IsType<RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Tiered>(gatedSite.Gate).Tier);
        var missing = row with { Construction = null };
        Assert.Throws<ObservationContractException>(() => ConstructionReads.ConstructionFromTables(new(Buildings.Of(missing), Pawns.Of(), Things.Of(), [])));

        var tables = new Tables(Buildings.Of(row), Pawns.Of(), Things.Of(), []) { ConstructionMemo = new() };
        Assert.Equal(7, ConstructionReads.ConstructionFromTables(tables).Deficit[Material]);
        var supplied = row with { Construction = ((RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build)row.Construction) with
            { Resources = [new RimGovernor.Brain.GameFacts.Observations.MaterialDeficit { DefName = Material.Value, StillNeeded = 0 }] } };
        var updated = ConstructionReads.ConstructionFromTables(tables with { Buildings = Buildings.Of(supplied) });
        Assert.Empty(updated.Deficit);
        Assert.True(Assert.Single(updated.Sites).ResourcesComplete);
    }
}
