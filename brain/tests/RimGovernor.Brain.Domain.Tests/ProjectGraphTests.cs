using O = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3530: the tier-2 project graph and crop growth by twelfth, on the devilstrand case (forecast value, "Devilstrand worked through").
public sealed class ProjectGraphTests
{
    // Every pawn here has 24 - 8 asleep (rest 1 : 2) - 0.4 eating (2 meals of 500 ticks) - 1.5 recreation (joy 1 : 15) = 14.1 free hours.
    private const double FreeHours = 14.1;

    private const double WorkTicksPerDay = FreeHours * 2500;

    // Growth a day at full temperature and fertility: the growing fraction of the day over the 22.5 grow days.
    private const double FullGrowth = 0.55 / 22.5;

    private static readonly ResearchProjectId Gate = new(ColonyModelFixtures.Project);

    private static readonly EquatableArray<string> AfterHarvest = EquatableArray.From(["harvest:zone1"]);

    internal static ModelPawn Pawn(string name, double? research, double? plants) => new()
    {
        Pawn = name,
        Needs = EquatableArray.From(new[]
        {
            new ModelNeed { Kind = "rest", Fall = 0.00001, Gain = 0.00002, Threshold = 0.28, Service = new ModelService.NoneObserved() },
            new ModelNeed { Kind = "food", Fall = 0.00002, Threshold = 0.3, Service = new ModelService.Observed(0.6, 400.0, 100.0), Gain = 0 },
            new ModelNeed { Kind = "joy", Fall = 0.00001, Gain = 0.00015, Threshold = 0.3, Service = new ModelService.NoneObserved() },
        }),
        Skills = EquatableArray.From(new[] { new O.Skill { Passion = O.Passion.None, DefName = "Plants", Level = 10 } }),
        ComfortableMin = 16.0,
        ComfortableMax = 26.0,
        ResearchSpeed = research ?? 0,
        PlantWorkSpeed = plants ?? 0,
        Consciousness = 1.0,
        Moving = 1.0,
        Manipulation = 1.0,
        Sight = 1.0,
        Talking = 1.0,
        Hunts = false,
    };

    // A year warm enough for devilstrand to be sown in spring: the fixture's temperatures, except that twelfths 10 and 11 stay at 12 C.
    // Twelfth 9 is 8 C, factor 0.8; twelfth 0 is -10 C, factor 0.
    private static readonly double[] Temperatures = [-10, -8, -2, 5, 12, 18, 22, 21, 15, 8, 12, 12];

    // Twelve full-fertility cells of soil, a research-only Anna and a grow-only Bo, 8 cloth and 120 steel in store.
    internal static ColonyModelInput Colony(int twelfth = 4, double? annaResearch = 1, double fertility = 1)
    {
        var input = ColonyModelFixtures.Devilstrand();
        var pawns = EquatableArray.From(new[] { Pawn("Anna", annaResearch, null), Pawn("Bo", null, 1) });
        return input with
        {
            Pawns = pawns,
            Climate = input.Climate with { CurrentTwelfth = twelfth, Temperatures = EquatableArray.From(Temperatures) },
            Land = EquatableArray.From(new[] { new ModelField { Id = "zone1", Fertility = EquatableArray.From(Enumerable.Repeat(fertility, 12)) } }),
            Stocks = new ModelStocks
            {
                Nutrition = 10_000.0,
                Materials = EquatableArray.From(new[] { new Amount(new Resource("Steel"), 120), new Amount(new Resource(ColonyModelFixtures.Cloth), 8) }),
                Medicine = EquatableArray.From(new[] { new Amount(new Resource("MedicineHerbal"), 13) }),
            },
        };
    }

    internal static ProjectGraph Chain(ColonyModelInput input, bool craft = true)
    {
        var chain = ProjectGraph.CropChain(input, ColonyModelFixtures.Crop, "zone1", [Gate]);
        return craft ? chain.Then(ProjectGraph.Craft(input, ColonyModelFixtures.Recipe, AfterHarvest, false)) : chain;
    }

    internal static ColonyTrajectory Run(ColonyModelInput input, ProjectGraph graph)
    {
        var cycles = input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray()).ToArray();
        return ColonyModel.Run(input, cycles, new ColonyModelPolicy { MedicineTendsPerDay = 0.0, Projects = graph });
    }

    private static ModelEvent At(ColonyTrajectory t, string id) => t.Events.Single(e => e.Id == id);

    [Fact]
    public void ACraftWithSeveralIngredientAlternativesConsumesTheNamedStuffAndNeedsNoFirstAlternativeFinding()
    {
        var input = Colony();
        // The game's apparel recipes list every stuff the garment can be made of in name order, cloth first, each at its own count.
        var recipe = new ProductionRecipeInput("Make_Apparel_Cape", EquatableArray.From(new[]
        {
            EquatableArray.From(new[] { new Amount(new Resource("Cloth"), 80), new Amount(new Resource(ColonyModelFixtures.Cloth), 50) }),
        }), false)
        {
            Products = EquatableArray.From(new[] { new Amount(new Resource("Apparel_Cape"), 1) }),
            WorkAmount = 10_000.0,
        };
        input = input with { Recipes = EquatableArray.From(new[] { recipe }) };

        Assert.Same(recipe, GoalPaths.RecipeFor(input, "Apparel_Cape", new Resource(ColonyModelFixtures.Cloth)));
        Assert.Null(GoalPaths.RecipeFor(input, "Apparel_Cape", new Resource("Steel")));
        var named = ProjectGraph.Craft(input, "Make_Apparel_Cape", AfterHarvest, false, 1, new Resource(ColonyModelFixtures.Cloth));
        var node = Assert.Single(named.Nodes);
        Assert.Equal([new Amount(new Resource(ColonyModelFixtures.Cloth), 50)], node.Ingredients);
        Assert.Empty(named.Findings);
        var unnamed = ProjectGraph.Craft(input, "Make_Apparel_Cape", AfterHarvest, false);
        Assert.Equal([new Amount(new Resource("Cloth"), 80)], Assert.Single(unnamed.Nodes).Ingredients);
        Assert.Equal(["craft_alternative_first:Make_Apparel_Cape"], unnamed.Findings);
    }

    [Fact]
    public void TheDevilstrandChainCompletesInSequenceAtTheHandCalculatedDays()
    {
        var input = Colony();
        var graph = Chain(input);
        Assert.Equal(["research:Devilstrand", "sow:zone1", "grow:zone1", "harvest:zone1", "craft:Make_Apparel_Duster"], graph.Nodes.Select(n => n.Id));
        Assert.Equal(600, graph.Nodes[0].Work, 9);

        var trajectory = Run(input, graph);

        // Research: 600 points left at 14.1 h x 2500 ticks x 0.00825 a day.
        var research = 600 / (WorkTicksPerDay * 0.00825);
        // Sow: 12 cells x 400 work by Bo alone.
        var sow = research + (4800 / WorkTicksPerDay);
        // Grow: 0.65 of the plant's growth. Twelfths 5 to 8 (to day 20.2) are at full temperature factor, twelfth 9 (to 25.2) at 0.8,
        // twelfth 10 at full again.
        var grown = ((20.2 - sow) * FullGrowth) + (5 * FullGrowth * 0.8);
        var grow = 25.2 + ((0.65 - grown) / FullGrowth);
        Assert.InRange(grow, 25.2, 30.2);
        // Harvest: 12 cells x 200 work by Bo, outdoors in the 9.5 daylight hours of twelfth 10.
        var harvest = grow + (2400 / (9.5 * 2500));
        // Craft: 10,000 work by both pawns, who can both craft.
        var craft = harvest + (10_000 / (2 * WorkTicksPerDay));

        Assert.Equal(ModelEventKind.ProjectCompletion, At(trajectory, "research:Devilstrand").Kind);
        Assert.Equal(ModelEventKind.Harvest, At(trajectory, "harvest:zone1").Kind);
        Assert.Equal(research, At(trajectory, "research:Devilstrand").Day, 6);
        Assert.Equal(sow, At(trajectory, "sow:zone1").Day, 6);
        Assert.Equal(grow, At(trajectory, "grow:zone1").Day, 6);
        Assert.Equal(harvest, At(trajectory, "harvest:zone1").Day, 6);
        Assert.Equal(craft, At(trajectory, "craft:Make_Apparel_Duster").Day, 6);
        Assert.InRange(harvest, 29, 30);

        // 12 cells x 6 fibre join 128 materials, the duster takes 80 of the cloth.
        var materials = (int)ModelStock.Materials;
        Assert.Equal(128, At(trajectory, "grow:zone1").Levels[materials], 6);
        Assert.Equal(200, At(trajectory, "harvest:zone1").Levels[materials], 6);
        Assert.Equal(120, At(trajectory, "craft:Make_Apparel_Duster").Levels[materials], 6);
        Assert.DoesNotContain(trajectory.Findings, f => f.StartsWith("project_", StringComparison.Ordinal) || f.StartsWith("crop_", StringComparison.Ordinal));
    }

    [Fact]
    public void ACropSownTooLateForTheSeasonNeverMaturesAndHarvestsNothing()
    {
        // Sown in twelfth 8: 0.2 days at full rate, five at 0.8, ten at full, then -10 C: 0.65 of growth is out of reach.
        var input = Colony(twelfth: 8);
        var trajectory = Run(input, ProjectGraph.CropChain(input, ColonyModelFixtures.Crop, "zone1"));

        Assert.Contains("crop_will_not_mature:grow:zone1", trajectory.Findings);
        Assert.Contains("project_blocked:harvest:zone1", trajectory.Findings);
        Assert.Contains(trajectory.Events, e => e.Id == "sow:zone1");
        Assert.DoesNotContain(trajectory.Events, e => e.Id is "grow:zone1" or "harvest:zone1");
        Assert.DoesNotContain(trajectory.Events, e => e.Kind == ModelEventKind.Harvest);
        Assert.Equal(128, trajectory.Events[^1].Levels[(int)ModelStock.Materials], 6);
    }

    [Fact]
    public void AChainWithNoPawnAbleToResearchNeverCompletesAndTheTrajectorySaysSo()
    {
        var input = Colony(annaResearch: null);
        var trajectory = Run(input, Chain(input));

        Assert.Contains("project_unstaffed:research:Devilstrand", trajectory.Findings);
        foreach (var id in new[] { "research:Devilstrand", "sow:zone1", "grow:zone1", "harvest:zone1", "craft:Make_Apparel_Duster" })
        {
            Assert.Contains($"project_incomplete:{id}", trajectory.Findings);
            Assert.DoesNotContain(trajectory.Events, e => e.Id == id);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void ResearchHasOneResearcherPerBenchTheResearchReadLists(int benches, int researchers)
    {
        var input = Colony();
        input = input with
        {
            Pawns = EquatableArray.From(new[] { Pawn("Anna", 1, null), Pawn("Bo", 1, null) }),
            Research = input.Research with { Benches = benches },
        };
        var trajectory = Run(input, ProjectGraph.ResearchChain(input, Gate));

        Assert.Equal(600 / (researchers * WorkTicksPerDay * 0.00825), At(trajectory, "research:Devilstrand").Day, 6);
    }

    [Fact]
    public void AColonyWithNoResearchBenchHasNoResearcherAndSaysSo()
    {
        var input = Colony();
        input = input with { Research = input.Research with { Benches = 0 } };
        var trajectory = Run(input, ProjectGraph.ResearchChain(input, Gate));

        Assert.Contains("project_unstaffed:research:Devilstrand", trajectory.Findings);
    }

    [Fact]
    public void PoorSoilThatCannotFinishBeforeTheColdNeverMatures()
    {
        // Mean fertility 0.5 with sensitivity 0.7 is 0.5 x 0.7 + 0.3 = 0.65 of the full rate; the same crop on full soil matures.
        var crop = Colony().Crops.Single();
        Assert.Equal(0.65, CropGrowthModel.FertilityFactor(crop, 0.5), 9);
        Assert.Equal(FullGrowth * 0.65, CropGrowthModel.DailyGrowth(crop, 0.5, 20), 9);

        var poor = Colony(fertility: 0.5);
        var trajectory = Run(poor, Chain(poor, craft: false));

        Assert.Contains("crop_will_not_mature:grow:zone1", trajectory.Findings);
        Assert.DoesNotContain(trajectory.Events, e => e.Id == "harvest:zone1");
    }

    [Fact]
    public void SowingTakesTheHoursOfEveryPawnAbleToSow()
    {
        var input = Colony();
        input = input with { Pawns = EquatableArray.From(new[] { Pawn("Anna", 1, 1), Pawn("Bo", null, 1) }) };
        var trajectory = Run(input, Chain(input, craft: false));

        var research = 600 / (WorkTicksPerDay * 0.00825);
        // Anna researches; once done she and Bo sow together.
        Assert.Equal(research + (4800 / (2 * WorkTicksPerDay)), At(trajectory, "sow:zone1").Day, 6);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 0.5)]
    [InlineData(10, 1)]
    [InlineData(30, 1)]
    [InlineData(42, 1)]
    [InlineData(50, 0.5)]
    [InlineData(58, 0)]
    [InlineData(70, 0)]
    public void TheTemperatureFactorFollowsTheGamesRampOnTheCropsBand(double temperature, double factor)
    {
        var crop = ColonyModelFixtures.Devilstrand().Crops.Single();

        Assert.Equal(factor, CropGrowthModel.TemperatureFactor(crop, temperature), 9);
    }

    [Fact]
    public void AnInvalidGrowthTemperatureBandRefusesTheCropProjection()
    {
        var input = Colony();
        var crop = input.Crops.Single() with { MaxGrowthTemperature = default };
        input = input with { Crops = EquatableArray.From(new[] { crop }) };

        Assert.Equal("crop growth temperature band", Assert.Throws<RequiredFactException>(() => Run(input, Chain(input, craft: false))).Fact);
    }
}
