using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FacilityPlanningRulesTests
{
    [Fact]
    public void HeatingControlReadsTheBuildingServiceWithoutABillStack()
    {
        var cell = new Cell(1, 1);
        var placement = new Building("InstalledFuelHeater", cell, Rotation.North, "");
        var heater = ConstructionObservationFixtures.Built(placement, "heater");
        heater = heater with { Observed = heater.Observed with { Service = heater.Observed.Service with
        {
            Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable
            {
                AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.Switch { Allowed = true },
                Burn = BuildingServiceWire.IdleBurn, Fuel = 1, TargetFuel = 10, AutoRefuelPercent = 0.3, OutOfFuel = false,
            },
        } } };
        var facts = ColonyFactsCoreTests.Reading().Facts;
        facts = facts with
        {
            CurrentConstruction = CurrentConstruction.Empty with { Buildings = [heater] },
            ConstructionClaims = [new() { Concern = ShelterFamily.Temperature.FilingKey, Building = placement, Cells = [cell] }],
            Upkeep = facts.Upkeep with { Rooms = RoomFixtures.Census((RoomFixtures.State("room"), [cell])) },
        };
        Assert.Empty(facts.EssentialInputs.Benches);
        Assert.Equal(new HeatCampfire { Id = "heater", Room = "room", AutoRefuel = true },
            Assert.Single(FacilityPlanningRules.HeatCampfires(facts, new HashSet<string> { placement.Definition })));
        facts = facts with { Upkeep = facts.Upkeep with { Rooms = RoomTestFacts.NoRooms } };
        Assert.Empty(FacilityPlanningRules.HeatCampfires(facts, new HashSet<string> { placement.Definition }));
    }

    [Fact]
    public void CookingWaitsForItsFoodPlanBeforeLookingForAReplacement()
    {
        var facts = ColonyFactsCoreTests.Reading().Facts with { Cooking = true };
        Assert.Equal(FacilityDemand.AwaitingFoodPlan, FacilityPlanningRules.CookingDemand(facts, "Campfire"));
        Assert.Equal(FacilityDemand.Unhosted, FacilityPlanningRules.ButcherDemand(facts));
    }

    [Fact]
    public void BuilderEligibilityPreservesMissingDefinitionAndResearchReasons()
    {
        var facts = ColonyFactsCoreTests.Reading().Facts;
        Assert.Equal(new FacilityEligibility(Cause.FieldUnavailable, "bench_definition"), FacilityPlanningRules.BuilderGate(facts, "bench"));
        facts = facts with { Definitions = [new() { Name = "bench", Available = false, Research = ["research-a", "research-b"] }] };
        Assert.Equal(new FacilityEligibility(Cause.AwaitingPlan, "research", "research-a+research-b"), FacilityPlanningRules.BuilderGate(facts, "bench"));
        facts = facts with { Definitions = [new() { Name = "bench", Available = false }] };
        Assert.Equal(new FacilityEligibility(Cause.AwaitingPlan, "bench", "unavailable"), FacilityPlanningRules.BuilderGate(facts, "bench"));
    }

    [Theory]
    [InlineData(0, false, false, true)]
    [InlineData(5, false, false, true)]
    [InlineData(6, false, false, false)]
    [InlineData(5, true, false, false)]
    [InlineData(5, false, true, false)]
    public void BuilderRequiresEnabledWorkAvailablePawnAndSufficientEnabledSkill(int minimum, bool disabled, bool downed, bool expected)
    {
        var pawn = PawnFixtures.Worker("builder");
        pawn = pawn with
        {
            Observed = pawn.Observed with
            {
                Downed = downed,
                Biography = pawn.Observed.Biography with { Skills = [new() { DefName = "Construction", Level = 5, Disabled = disabled, Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None }] },
            },
            Work = PawnFixtures.WorkTable(true, [new() { Observed = new() { DefName = "Construction", Priority = 1, Disabled = disabled } }]),
        };
        Assert.Equal(expected, FacilityPlanningRules.BuilderAvailable([pawn], minimum));
        Assert.False(FacilityPlanningRules.BuilderAvailable([], minimum));
    }

    [Fact]
    public void AdvancedLaboratoryPrecedesAnalyzerAndReadsGeometryOnlyWhenSupported()
    {
        var facts = ColonyFactsCoreTests.Reading().Facts;
        Assert.Equal(("", false), FacilityPlanningRules.AdvancedLabOwed(facts, null));
        facts = facts with { Shapes = facts.Shapes with { Furniture = facts.Shapes.Furniture with
            { AdvancedLab = "advanced", Analyzer = new() { Def = "analyzer" } } },
            Definitions = [new() { Name = "advanced", Available = true }, new() { Name = "analyzer", Available = true }] };
        Assert.Throws<RequiredFactException>(() => FacilityPlanningRules.AdvancedLabOwed(facts, null));
        Assert.Equal(("advanced", true), FacilityPlanningRules.AdvancedLabOwed(facts, []));
        SiteCell Standing(string definition) => SiteCellFixtures.At(new Cell(1, 1), things:
            [new Thing { Def = definition, Flags = ThingMarks.Edifice, Faction = ThingFaction.Player }]);
        Assert.Equal(("analyzer", true), FacilityPlanningRules.AdvancedLabOwed(facts, [Standing("advanced")]));
        Assert.Equal(("", false), FacilityPlanningRules.AdvancedLabOwed(facts, [Standing("advanced"), Standing("analyzer")]));
    }
    [Fact]
    public void AStandingButcherBenchIsTheFacilityAndATableNeverRetiresTheSpotHere()
    {
        var facts = ColonyFactsCoreTests.Reading().Facts;
        // No bench: the butchering load is the layout comparison's to host; no definition is chosen.
        Assert.Equal(FacilityDemand.Unhosted, FacilityPlanningRules.ButcherDemand(facts));
        Assert.False(Rules.ButcherFacilityMet(facts));
        facts = facts with { EssentialInputs = facts.EssentialInputs with { Benches =
            [ProductionBenchFixtures.Butchering("spot", Rules.ButcherSpotDefinition, "spot", true)] } };
        Assert.Equal(FacilityDemand.Existing, FacilityPlanningRules.ButcherDemand(facts));
        Assert.True(Rules.ButcherFacilityMet(facts));
        // A table beside the spot owes no retirement: removing a host is the comparison's later decision.
        facts = facts with { EssentialInputs = facts.EssentialInputs with { Benches =
            [.. facts.EssentialInputs.Benches, ProductionBenchFixtures.Butchering("table", KitchenSeparationRules.ButcherTableDefinition, "table", true)] } };
        Assert.Equal(FacilityDemand.Existing, FacilityPlanningRules.ButcherDemand(facts));
        Assert.True(Rules.ButcherFacilityMet(facts));
    }
}
