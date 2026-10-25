using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ConstructionNativeObservationTests
{
    [Theory]
    [InlineData(Rotation.North)]
    [InlineData(Rotation.East)]
    [InlineData(Rotation.South)]
    [InlineData(Rotation.West)]
    public void BuiltCensusRetainsCanonicalRowAndReadsNativeGeometry(Rotation rotation)
    {
        var building = new Building("Wall", new(4, 5), rotation, "WoodLog");
        var row = ConstructionObservationFixtures.Row(building, "wall", [new(3, 5), new(4, 5), new(5, 5)]);
        var census = ConstructionReads.ConstructionFromTables(new(Buildings.Of(row), Pawns.Of(), Things.Of(), []));
        var built = Assert.Single(census.Buildings);
        Assert.Same(row, built.Observed);
        Assert.Equal("wall", built.Id);
        Assert.Equal(building, built.Building);
        Assert.Equal([new Cell(3, 5), new Cell(4, 5), new Cell(5, 5)], built.Cells);
    }

    [Fact]
    public void SiteReadsNativeIdentityDefinitionMaterialsAndFinishingGuards()
    {
        var building = new Building("Bed", new(4, 5), Rotation.East, "WoodLog");
        var initial = ConstructionObservationFixtures.Site(building, id: "frame", stage: "frame", qualitySensitive: true, nativeFinishingSkill: 4);
        var row = initial.Observed with { Building = initial.Observed.Building with { DefName = "Frame_Bed" } };
        var site = new ConstructionSite { Observed = row };
        Assert.Same(row, site.Observed);
        Assert.Same(row.Construction, site.Construction);
        Assert.Equal("frame", site.Id);
        Assert.Equal("frame", site.Stage);
        Assert.Equal(building, site.Building);
        Assert.False(site.ResourcesComplete);
        Assert.Equal(4, site.NativeFinishingSkill);
        Assert.True(site.QualitySensitive);
        Assert.Null(site.Minimum);
        Assert.Equal("minimum_not_set", site.FinishingBlocker);

        var minimum = new RimGovernor.Brain.GameFacts.Observations.FinishingMinimum { Skill = 6, EligibleFinishers = 0 };
        var gate = new RimGovernor.Brain.GameFacts.Observations.ConstructionGate.Tiered { Tier = ConstructionTier.Comfort };
        var guarded = site with { Observed = row with { Construction = site.Construction with { Minimum = minimum, Gate = gate } } };
        Assert.Same(minimum, guarded.Minimum);
        Assert.Same(gate, guarded.Gate);
        Assert.Equal("no_qualified_builder", guarded.FinishingBlocker);

        var supplied = guarded with
        {
            Observed = guarded.Observed with
            {
                Construction = guarded.Construction with
                {
                    Resources = [.. guarded.Construction.Resources.Select(r => r with { StillNeeded = 0 })],
                    Minimum = minimum with { EligibleFinishers = 1 },
                },
            },
        };
        Assert.True(supplied.ResourcesComplete);
        Assert.Equal("", supplied.FinishingBlocker);
    }

    [Fact]
    public void NativeAbsenceAndInvalidSiteStateAreNotInvented()
    {
        var initial = ConstructionObservationFixtures.Site(new Building("Wall", new(1, 1), Rotation.North, ""));
        Assert.Null(initial.Minimum);
        Assert.Equal("", initial.FinishingBlocker);
        var missing = initial with { Observed = initial.Observed with { Construction = null } };
        Assert.Throws<DomainException>(() => missing.Construction);
        var built = initial with { Observed = initial.Observed with { Status = RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built } };
        Assert.Throws<DomainException>(() => built.Stage);
    }
}
