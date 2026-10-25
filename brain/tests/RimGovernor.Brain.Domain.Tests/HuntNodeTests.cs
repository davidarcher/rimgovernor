using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3620: tier 2's hunt-and-butcher node, built from the wildlife census with the hunt planner's work and the race's butchery yields.
public sealed class HuntNodeTests
{
    private const string Leather = "Leather_Plain";

    // A deer's race row: 39.6 leather (LeatherAmount) and 82 meat (MeatAmount) at 0.05 nutrition a unit.
    private static readonly AnimalRace Deer = new()
    {
        Def = new Resource("Deer"),
        Butchery = EquatableArray.From(new[] { new SourceProduct(new Resource(Leather), 39.6) }),
        Meat = new RaceMeat.Yields(82.0, 0.05),
    };

    private static double MeatNutrition(AnimalRace race) => race.Meat is RaceMeat.Yields { Amount: var amount, NutritionPerUnit: var per } ? amount * per : 0;

    // A census row as the acquisition decode makes it from the race row: the meat's nutrition and the race's butchery products.
    private static AcquisitionSource Animal(AnimalRace race, int i) => new()
    {
        Observed = new() { Source = new() { Id = $"{race.Def}{i}" }, Token = $"{race.Def}{i}", Resource = race.Def.Value, Yield = 1, Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Open(), Taken = false, Kind = new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt(82, 1, false, false, false, false) },
        Entity = new() { Id = $"{race.Def}{i}", DefName = race.Def.Value, Label = race.Def.Value, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new Cell(i, 0)) },
        NutritionYield = MeatNutrition(race),
        Products = race.Butchery,
    };

    private static AcquisitionSource[] Herd(int animals) => [.. Enumerable.Range(0, animals).Select(i => Animal(Deer, i))];

    // Anna researches and may not be known to hunt; Bo sows and hunts.
    private static ColonyModelInput Colony(params AcquisitionSource[] census)
    {
        var input = ProjectGraphTests.Colony();
        var pawns = EquatableArray.From(new[] { ProjectGraphTests.Pawn("Anna", 1, null), ProjectGraphTests.Pawn("Bo", null, 1) with { Hunts = true } });
        return input with { Pawns = pawns, Wildlife = HuntSquadRules.Herds(census) };
    }

    [Fact]
    public void AHerdOfDeerGivesALeatherArrivalDayAndANutritionGainInTheTrajectory()
    {
        var input = Colony(Herd(5));
        var hunts = ProjectGraph.Hunts(input);
        var node = Assert.Single(hunts.Nodes);
        Assert.Equal((ProjectGraph.HuntId(Deer.Def), ProjectKind.Hunt), (node.Id, node.Kind));
        Assert.Equal(["butcher_work_unread"], hunts.Findings);

        var trajectory = ProjectGraphTests.Run(input, hunts);
        var without = ProjectGraphTests.Run(input, new ProjectGraph());

        // Five deer at the hunt planner's 7,500 pursuit ticks each, worked by Bo alone in the daylight hours of twelfth 4.
        var bo = PawnCycle.Year(input, input.Pawns[1]).ToArray()[4];
        var arrival = 5 * FoodPlanRules.FoodHuntWorkTicks / (Assert.IsType<PawnCycle.Scheduled>(bo).OutdoorHours * GameTime.TicksPerHour);
        var hunted = trajectory.Events.Single(e => e.Id == node.Id);
        Assert.Equal(ModelEventKind.ProjectCompletion, hunted.Kind);
        Assert.Equal(arrival, hunted.Day, 6);
        Assert.True(arrival > 1, "the leather arrives beyond tier 1's one-day horizon");

        // The meat raises nutrition and the leather joins materials, against the same run without the hunt.
        Assert.Equal(5 * 82 * 0.05, trajectory.Events[^1].Levels[(int)ModelStock.Nutrition] - without.Events[^1].Levels[(int)ModelStock.Nutrition], 6);
        Assert.Equal(198, trajectory.Events[^1].Levels[(int)ModelStock.Materials] - without.Events[^1].Levels[(int)ModelStock.Materials], 6);
    }

    [Fact]
    public void TheHuntedLeatherIsAStockACraftTakes()
    {
        var input = Colony(Herd(2));
        var jacket = new ProductionRecipeInput("Make_Apparel_Jacket", EquatableArray.From(new[] { EquatableArray.From(new[] { new Amount(new Resource(Leather), 70) }) }), false)
        {
            Products = EquatableArray.From(new[] { new Amount(new Resource("Apparel_Jacket"), 1) }),
            WorkAmount = 5_000.0,
        };
        input = input with { Recipes = EquatableArray.From(new[] { jacket }) };
        var hunt = ProjectGraph.HuntId(Deer.Def);
        var graph = ProjectGraph.Hunts(input).Then(ProjectGraph.Craft(input, jacket.Recipe, EquatableArray.From(new[] { hunt }), false));

        var trajectory = ProjectGraphTests.Run(input, graph);

        var made = Assert.Single(trajectory.Apparel);
        Assert.True(made.Day > trajectory.Events.Single(e => e.Id == hunt).Day);
        Assert.DoesNotContain(trajectory.Findings, f => f.StartsWith("project_", StringComparison.Ordinal));
    }

    [Fact]
    public void NoHuntableAnimalsMeansNoNode()
    {
        // A pest is hunted for the trees it eats, not for food; a plant is no hunt.
        var animal = Animal(Deer, 0);
        var pest = animal with { Observed = animal.Observed with { Source = new() { Id = "boomrat" }, Token = "boomrat", Resource = "Boomrat" }, Entity = animal.Entity with { Id = "boomrat", DefName = "Boomrat", Label = "boomrat" }, Pest = true, NutritionYield = 0 };
        var bush = new AcquisitionSource
        {
            Observed = new() { Source = new() { Id = "berry" }, Token = "berry", Resource = "RawBerries", Yield = 10, Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Open(), Taken = false, Kind = new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Plant(true, 1, false) },
            Entity = new() { Id = "berry", DefName = "Plant_Berry", Label = "berry bush", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new Cell(0, 0)) },
            NutritionYield = 0.5,
        };
        var input = Colony(pest, bush);

        Assert.Empty(input.Wildlife);
        var graph = ProjectGraph.Standing(input, [], ColonyModelFixtures.NoThreat);
        Assert.DoesNotContain(graph.Nodes, n => n.Kind == ProjectKind.Hunt);
        Assert.DoesNotContain("butcher_work_unread", graph.Findings);
        Assert.Empty(ProjectGraph.Hunts(input with { Wildlife = default }).Nodes);
    }

    [Fact]
    public void TheYieldsMatchTheRaceDef()
    {
        var herd = Assert.Single(HuntSquadRules.Herds(Herd(3)));
        Assert.Equal(3, herd.Animals);
        Assert.Equal(3 * MeatNutrition(Deer), herd.Nutrition, 9);
        var leather = Assert.Single(herd.Products);
        Assert.Equal((Deer.Butchery[0].Def, 3 * Deer.Butchery[0].Amount), (leather.Def, leather.Amount));

        var node = Assert.Single(ProjectGraph.Hunts(Colony(Herd(3))).Nodes);
        Assert.Equal(herd.Nutrition, node.Nutrition, 9);
        Assert.Equal([new Amount(new Resource(Leather), 119)], node.Products);
    }

    [Fact]
    public void TheReviewFrameCarriesTheWildlifeCensusAndAnUnarmedPawnIsNoHunter()
    {
        var projection = ColonyModelFixtures.Reading() with
        {
            Facts = ColonyModelFixtures.Reading().Facts with
            {
                Acquisition = EquatableArray.From(Herd(2))
            }
        };

        var input = ColonyModelFixtures.ReadyOf(projection);

        var herd = Assert.Single(input.Wildlife);
        Assert.Equal((Deer.Def, 2, 2 * FoodPlanRules.FoodHuntWorkTicks), (herd.Race, herd.Animals, herd.Work));
        Assert.False(Assert.Single(input.Pawns).Hunts);
    }
}
