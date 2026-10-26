using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FacilityPlanningRulesTests
{
    [Fact]
    public void CookingWaitsForItsFoodPlanBeforeLookingForAReplacement()
    {
        var facts = ColonyFactsCoreTests.Reading().Facts with { Cooking = true };
        Assert.Equal(FacilityDemand.AwaitingFoodPlan, FacilityPlanningRules.CookingDemand(facts, "Campfire"));
        Assert.Equal(FacilityDemand.ButcherSpot, FacilityPlanningRules.ButcherDemand(facts));
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
    public void EveryGeneratorRowMustBeReadEvenAfterABuildableGenerator()
    {
        string[] generators = ["InstalledGeneratorA", "InstalledGeneratorB"];
        var available = generators.ToDictionary(x => x, _ => false);
        Assert.False(FacilityPlanningRules.GeneratorAvailable(available, generators));
        available[generators[0]] = true;
        Assert.True(FacilityPlanningRules.GeneratorAvailable(available, generators));
        available.Remove(generators[^1]);
        Assert.Throws<RequiredFactException>(() => FacilityPlanningRules.GeneratorAvailable(available, generators));
    }

    [Fact]
    public void CampfireIntentRequiresConstructionCensus()
    {
        Assert.Throws<RequiredFactException>(() => FacilityPlanningRules.CampfireIntentStanding([], null, "Campfire"));
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
    public void ButcherSpotRetirementPrecedesTableReplacementAndPreservesBenchOrder()
    {
        var spot = ConstructionObservationFixtures.Built(new Building(Rules.ButcherSpotDefinition, new Cell(1, 1), Rotation.North, ""), "spot");
        var facts = ColonyFactsCoreTests.Reading().Facts;
        facts = facts with
        {
            EssentialInputs = facts.EssentialInputs with { Benches =
                [ProductionBenchFixtures.Butchering("spot", Rules.ButcherSpotDefinition, "spot", true)] },
            CurrentConstruction = CurrentConstruction.Empty with { Buildings = [spot] },
        };
        Assert.Equal(FacilityDemand.Existing, FacilityPlanningRules.ButcherDemand(facts));
        facts = facts with { EssentialInputs = facts.EssentialInputs with { Benches =
            [.. facts.EssentialInputs.Benches, ProductionBenchFixtures.Butchering("table", KitchenSeparationRules.ButcherTableDefinition, "table", true)] } };
        Assert.Equal(FacilityDemand.RetireButcherSpot, FacilityPlanningRules.ButcherDemand(facts));
        Assert.Equal(spot, FacilityPlanningRules.StandingButcherSpot(facts));
    }

    [Fact]
    public void CookingIntentDoesNotMistakeHeatCampfireForCookingFacility()
    {
        var campfire = new Building("Campfire", new Cell(2, 3), Rotation.North, "");
        var census = CurrentConstruction.Empty with { Buildings = [ConstructionObservationFixtures.Built(campfire, "fire")] };
        Assert.True(FacilityPlanningRules.CampfireIntentStanding([], census, "Campfire"));
        var heat = new ConstructionClaim { Concern = ShelterFamily.Temperature.FilingKey, Building = campfire, Cells = [campfire.Cell] };
        Assert.False(FacilityPlanningRules.CampfireIntentStanding([heat], census, "Campfire"));
    }
}
