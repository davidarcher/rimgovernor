using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3622: tier 2's garment nodes, one per reachable stuff and gear slot, each valued from the day it is made and only for what it adds
// over what its wearer wore before. The colony is ProjectGraphTests' devilstrand case: 12 cells of devilstrand yield 72 cloth, a duster
// takes 80 and 8 are in store.
public sealed class GarmentNodeTests
{
    private const double Horizon = CalendarRules.YearDays;

    private static readonly Resource Devilstrand = new(ColonyModelFixtures.Cloth);

    private static readonly Resource Cloth = new("Cloth");

    private static readonly Resource Leather = new("Leather_Plain");

    private static GearOption Bill(string def, Resource stuff, double sharp, GearSlot slot = GearSlot.Outer) => ForecastValueTests.Duster(sharp) with
    {
        ID = $"bill:{def}/{stuff}", Definition = new Resource(def), Stuff = stuff, Source = GearSource.Bill, Slot = slot,
    };

    private static ProductionRecipeInput Recipe(string def, Resource stuff, long units, double work = 10_000) =>
        new($"Make_{def}_{stuff}", EquatableArray.From(new[] { EquatableArray.From(new[] { new Amount(stuff, units) }) }), false)
        {
            Products = EquatableArray.From(new[] { new Amount(new Resource(def), 1) }),
            WorkAmount = work,
        };

    // The worked colony with devilstrand sown on zone1, the stock given and the recipes given.
    private static ColonyModelInput Colony(long devilstrand, long cloth, params ProductionRecipeInput[] recipes)
    {
        var input = ProjectGraphTests.Colony();
        return input with
        {
            Land = EquatableArray.From(input.Land.Select(f => f with { Crop = ColonyModelFixtures.Crop })),
            Stocks = input.Stocks with { Materials = EquatableArray.From(new[] { new Amount(Devilstrand, devilstrand), new Amount(Cloth, cloth) }) },
            Recipes = EquatableArray.From(recipes),
        };
    }

    // Each wearer starts in a 0.3 duster in the outer slot.
    private static ColonyValue Value(IReadOnlyList<GearOption> options, double horizon = Horizon, params string[] wearers)
    {
        wearers = wearers.Length == 0 ? ["Anna"] : wearers;
        var pawns = wearers.Select(name => ForecastValueTests.HarmPawn() with { Pawn = name }).ToArray();
        var worn = EquatableArray.From(new[] { ForecastValueTests.Duster(0.3) with { Slot = GearSlot.Outer } });
        var context = ForecastValueTests.Context(pawns[0], ForecastValueTests.HarmTables(), ForecastValueTests.RaidDay(), wearers.ToDictionary(n => n, _ => worn, StringComparer.Ordinal)) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [.. pawns] },
        };
        return new ColonyValue(context, options, horizon, [ProjectGraphTests.Pawn("Anna", 1, 1)], new ItemFacts());
    }

    private static ApparelMade Made(string def, Resource stuff, double day) =>
        new() { Day = day, Node = ProjectGraph.GarmentId(stuff, GearSlot.Outer), Products = [new Amount(new Resource(def), 1)], Ingredients = [new Amount(stuff, 80)] };

    private static double Of(ColonyValue value, params ApparelMade[] made) => value.Price(new ColonyTrajectory { Apparel = EquatableArray.From(made) }).Value;

    [Fact]
    public void AGrowingFieldWithNoClothInStockValuesTheClothGarmentOnlyFromItsHarvest()
    {
        var option = Bill("Apparel_Duster", Devilstrand, 0.8);
        var input = Colony(8, 0, Recipe("Apparel_Duster", Devilstrand, 80));
        var graph = ProjectGraph.Standing(input, [option], ColonyModelFixtures.NoThreat);
        var garment = ProjectGraph.GarmentId(Devilstrand, GearSlot.Outer);
        Assert.Contains(graph.Nodes, n => n.Id == garment);

        var trajectory = ProjectGraphTests.Run(input, graph);

        // The harvest's 72 cloth arrive on its day, and the duster is made from them after it.
        var harvest = Assert.Single(trajectory.Arrivals, a => a.Node == "harvest:zone1");
        Assert.Equal((Devilstrand, 72.0), (harvest.Resource, harvest.Count));
        Assert.Equal(trajectory.Events.Single(e => e.Id == "harvest:zone1").Day, harvest.Day);
        var made = Assert.Single(trajectory.Apparel);
        Assert.Equal(garment, made.Node);
        Assert.True(made.Day > harvest.Day);

        // The value is a daily gain times the days from the craft: two horizons give the same gain a day.
        var value = Value([option]).Price(trajectory).Value - Value([option]).Price(new ColonyTrajectory()).Value;
        var midway = (made.Day + Horizon) / 2;
        var shorter = Value([option], midway).Price(trajectory).Value - Value([option], midway).Price(new ColonyTrajectory()).Value;
        Assert.True(value > 0);
        Assert.Equal(value / (Horizon - made.Day), shorter / (midway - made.Day), 9);
    }

    [Fact]
    public void AHerdGivesLeatherGearAValueFromTheHuntArrival()
    {
        var option = Bill("Apparel_Jacket", Leather, 0.6);
        var deer = new AcquisitionSource { Id = "deer0", Definition = "Deer", Hunt = true, Food = true, NutritionYield = 4.1, Products = [new SourceProduct(Leather, 39.6)] };
        var input = Colony(8, 0, Recipe("Apparel_Jacket", Leather, 70, 5_000)) with
        {
            Land = default,
            Wildlife = HuntSquadRules.Herds([deer, deer with { Id = "deer1" }]),
        };
        input = input with { Pawns = EquatableArray.From(input.Pawns.Select(p => p with { Hunts = true })) };
        var graph = ProjectGraph.Standing(input, [option], ColonyModelFixtures.NoThreat);

        var trajectory = ProjectGraphTests.Run(input, graph);

        var hunt = Assert.Single(trajectory.Arrivals, a => a.Node == ProjectGraph.HuntId(new Resource("Deer")));
        Assert.Equal((Leather, 79.0), (hunt.Resource, hunt.Count));
        var made = Assert.Single(trajectory.Apparel);
        Assert.Equal(ProjectGraph.GarmentId(Leather, GearSlot.Outer), made.Node);
        Assert.True(made.Day > hunt.Day);
        Assert.True(Value([option]).Price(trajectory).Value > Value([option]).Price(new ColonyTrajectory()).Value);
    }

    [Fact]
    public void AnEarlierWeakerStuffIsValuedUntilALaterStrongerOneArrivesThenOnlyTheUpgradeCounts()
    {
        var cloth = Bill("Apparel_Duster", Cloth, 0.5);
        var devilstrand = Bill("Apparel_Duster", Devilstrand, 0.8);
        var value = Value([cloth, devilstrand]);
        var none = Of(value);
        var weak = Of(value, Made("Apparel_Duster", Cloth, 10)) - none;
        var strong = Of(value, Made("Apparel_Duster", Devilstrand, 50)) - none;

        // Cloth is worn from day 10 and devilstrand replaces it on day 50: each day before is the cloth's gain, each day after the devilstrand's.
        var both = Of(value, Made("Apparel_Duster", Cloth, 10), Made("Apparel_Duster", Devilstrand, 50)) - none;
        var clothPerDay = weak / (Horizon - 10);
        var devilstrandPerDay = strong / (Horizon - 50);
        Assert.True(devilstrandPerDay > clothPerDay && clothPerDay > 0);
        Assert.Equal((clothPerDay * 40) + (devilstrandPerDay * (Horizon - 50)), both, 6);

        // A weaker garment made after the stronger one is not worn.
        Assert.Equal(Of(value, Made("Apparel_Duster", Devilstrand, 10)), Of(value, Made("Apparel_Duster", Devilstrand, 10), Made("Apparel_Duster", Cloth, 50)), 9);
    }

    // #3623: the runway's chosen stuff is the stuff of the garment the walk leaves on the pawn's slot, among stuffs arriving within the clothing horizon.
    [Fact]
    public void TheClothingStuffIsTheLastGarmentTheWalkPutsOnThePawnsSlotWhoseStuffArrivesInTime()
    {
        var value = Value([Bill("Apparel_Duster", Cloth, 0.5), Bill("Apparel_Duster", Devilstrand, 0.8)]);
        var anna = (new PawnId("Anna"), GearSlot.Outer);
        IReadOnlyDictionary<(PawnId, GearSlot), Resource> Chosen(EquatableArray<ResourceArrival> arrivals, params ApparelMade[] made) =>
            value.ClothingStuff(new ColonyTrajectory { Apparel = EquatableArray.From(made), Arrivals = arrivals }, ClothingDemandRules.ClothingHorizonDays);
        ResourceArrival Harvest(Resource stuff, double day) => new() { Day = day, Node = "harvest:zone1", Resource = stuff, Count = 80 };

        // Stocked cloth is worn from day 2, and devilstrand harvested on day 10 replaces it on day 12.
        Assert.Equal(Devilstrand, Chosen([Harvest(Devilstrand, 10)], Made("Apparel_Duster", Cloth, 2), Made("Apparel_Duster", Devilstrand, 12))[anna]);
        // Devilstrand harvested after the horizon is not the runway's: the cloth worn before it is.
        Assert.Equal(Cloth, Chosen([Harvest(Devilstrand, 20)], Made("Apparel_Duster", Cloth, 2), Made("Apparel_Duster", Devilstrand, 22))[anna]);
        // A garment no better than the worn one is not worn, so the slot has no winner.
        Assert.Empty(Value([Bill("Apparel_Duster", Cloth, 0.2)]).ClothingStuff(new ColonyTrajectory { Apparel = [Made("Apparel_Duster", Cloth, 2)] }, ClothingDemandRules.ClothingHorizonDays));
    }

    [Fact]
    public void WithClothInStockTheFieldIsCreditedOnlyWithWhatItAddsBeyondTheStocksGarment()
    {
        var option = Bill("Apparel_Duster", Devilstrand, 0.8);
        var recipe = Recipe("Apparel_Duster", Devilstrand, 80);
        ProjectMarginals Marginals(ColonyModelInput input) => ProjectMarginalValue.Of(input,
            new ColonyModelPolicy { MedicineTendsPerDay = 0.0, Projects = ProjectGraph.Standing(input, [option], ColonyModelFixtures.NoThreat) }, Value([option]));

        var bare = Marginals(Colony(8, 0, recipe));
        var stocked = Marginals(Colony(80, 0, recipe));

        // Without the stock the duster waits on the harvest, so sowing earlier is worth its days; the stock makes it now, and the field adds nothing.
        Assert.True(bare.PerHour["sow:zone1"] > 0);
        Assert.Equal(0, stocked.PerHour["sow:zone1"]);
        Assert.True(stocked.Value > bare.Value);
        Assert.True(stocked.Trajectory.Apparel.Single().Day < stocked.Trajectory.Events.Single(e => e.Id == "harvest:zone1").Day);
    }

    [Fact]
    public void AStockCoveredGarmentPrunesAWeakerOneOfItsSlotButNotAStrongerOneThatArrivesLater()
    {
        // Cloth is in stock for a 0.5 duster; devilstrand comes from the field.
        var input = Colony(8, 80, Recipe("Apparel_Duster", Cloth, 80), Recipe("Apparel_Duster", Devilstrand, 80), Recipe("Apparel_Parka", Devilstrand, 80));
        var sources = ProjectGraph.Standing(input, [], ColonyModelFixtures.NoThreat);
        var cloth = Bill("Apparel_Duster", Cloth, 0.5);
        string[] Garments(params GearOption[] options) =>
            [.. ProjectGraph.Garments(input, sources, options, ColonyModelFixtures.NoThreat).Nodes.Where(n => n.Kind == ProjectKind.Craft).Select(n => n.Id)];

        // A stronger devilstrand duster stays: the cloth one is worn until it arrives.
        Assert.Equal([ProjectGraph.GarmentId(Devilstrand, GearSlot.Outer), ProjectGraph.GarmentId(Cloth, GearSlot.Outer)], Garments(cloth, Bill("Apparel_Duster", Devilstrand, 0.8)));
        // A weaker one arrives no earlier than the stocked cloth, so it is dropped.
        Assert.Equal([ProjectGraph.GarmentId(Cloth, GearSlot.Outer)], Garments(cloth, Bill("Apparel_Duster", Devilstrand, 0.4)));
        // Another slot is not compared.
        Assert.Equal(2, Garments(cloth, Bill("Apparel_Duster", Devilstrand, 0.4, GearSlot.MiddleTorso)).Length);
    }

    [Fact]
    public void WithinAStuffAndSlotTheMostArmorPicksTheDefAndAnUnreachableStuffHasNoNode()
    {
        var input = Colony(8, 0, Recipe("Apparel_Duster", Devilstrand, 80), Recipe("Apparel_Parka", Devilstrand, 80), Recipe("Apparel_Duster", Cloth, 80));
        var sources = ProjectGraph.Standing(input, [], ColonyModelFixtures.NoThreat);
        var graph = ProjectGraph.Garments(input, sources,
            [Bill("Apparel_Duster", Devilstrand, 0.8), Bill("Apparel_Parka", Devilstrand, 0.9), Bill("Apparel_Duster", Cloth, 0.99), Bill("Apparel_Cape", Devilstrand, 0, GearSlot.Belt)], ColonyModelFixtures.NoThreat);

        var node = Assert.Single(graph.Nodes, n => n.Kind == ProjectKind.Craft);
        Assert.Equal(ProjectGraph.GarmentId(Devilstrand, GearSlot.Outer), node.Id);
        Assert.Equal([new Amount(new Resource("Apparel_Parka"), 1)], node.Products);
        Assert.Empty(graph.Findings);
    }
}
