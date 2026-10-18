using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Amount = RimGovernor.Brain.Policy.Amount;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Gear stuff is chosen by stats among obtainable stuffs, cost breaking ties (#2467).</summary>
public sealed class GearStuffChoiceTests
{
    private static readonly Resource Steel = new("Steel");
    private static readonly Resource Plasteel = new("Plasteel");

    private static GearOption Shirt(string stuff, double sharp, double cost = 20) => new()
    {
        ID = "bill:Shirt/" + stuff,
        Definition = new("Shirt"),
        Stuff = new(stuff),
        Slot = GearSlot.SkinTorso,
        Layers = ["OnSkin"],
        Groups = ["Torso"],
        Source = GearSource.Bill,
        Quality = 2,
        Condition = 1,
        Sharp = sharp,
        Cost = cost,
        Ingredients = [new Amount(new(stuff), 50)],
    };

    private static RecipeHost Makes(Resource product, bool available, params Resource[] from) => new()
    {
        Definition = "Make" + product.Value,
        Products = [product],
        Available = available,
        Ingredients = Known(EquatableArray.From(from.Select(r => EquatableArray.From([new Amount(r, 10)])))),
    };

    private static GearObtainability Obtainability(Dictionary<Resource, long> stock, params RecipeHost[] recipes) =>
        new(stock, r => [.. recipes.Where(h => h.Products.Contains(r))]);

    private static Resource Planned(params GearOption[] options) =>
        GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput { Ambient = 20, ComfortableMin = 10, ComfortableMax = 30, Options = EquatableArray.From(options) })
            .Target.Single().Stuff;

    [Fact]
    public void AnUnobtainableBetterStuffIsNotChosen()
    {
        GearOption[] options = [Shirt("Plasteel", .5), Shirt("Steel", .2)];
        Assert.Equal(Plasteel, Planned(options)); // Stats alone pick the plasteel.
        var obtainable = Obtainability(new() { [Steel] = 100 });
        Assert.Equal(Steel, Planned([.. options.Select(o => o with { Unobtainable = !obtainable.Obtainable(o) })]));
    }

    [Fact]
    public void AStockShortOfTheBillIsNotObtainable()
    {
        Assert.False(Obtainability(new() { [Plasteel] = 49 }).Obtainable(Shirt("Plasteel", .5)));
        Assert.True(Obtainability(new() { [Plasteel] = 50 }).Obtainable(Shirt("Plasteel", .5)));
    }

    [Fact]
    public void AKnownRecipeChainMakesAStuffObtainable()
    {
        var plasteel = Shirt("Plasteel", .5);
        var stock = new Dictionary<Resource, long> { [Steel] = 100 };
        Assert.True(Obtainability(stock, Makes(Plasteel, true, Steel)).Obtainable(plasteel));
        // Research not finished, or an ingredient neither stocked nor made, or only a cycle: no route.
        Assert.False(Obtainability(stock, Makes(Plasteel, false, Steel)).Obtainable(plasteel));
        Assert.False(Obtainability(stock, Makes(Plasteel, true, new Resource("Uranium"))).Obtainable(plasteel));
        Assert.False(Obtainability(new(), Makes(Plasteel, true, Steel), Makes(Steel, true, Plasteel)).Obtainable(plasteel));
        // Two links deep: steel made from stocked ore.
        Assert.True(Obtainability(new() { [new("Ore")] = 5 }, Makes(Plasteel, true, Steel), Makes(Steel, true, new Resource("Ore"))).Obtainable(plasteel));
    }

    [Fact]
    public void AmongEqualStatsTheCheaperStuffWins()
    {
        // By id the cloth shirt would come first; the synthread one costs less at the same stats.
        Assert.Equal(new Resource("Synthread"), Planned(Shirt("Cloth", .2, cost: 30), Shirt("Synthread", .2, cost: 10)));
    }

    [Fact]
    public void TheBillFilterAdmitsOnlyCategoryMatesCostingNoMoreAndPicksTheCheapestElsewhere()
    {
        var metal = new Dictionary<Resource, IReadOnlyList<string>>
        {
            [Steel] = ["Metallic"], [Plasteel] = ["Metallic"], [new("Silver")] = ["Metallic"], [new("Gold")] = ["Metallic"], [new("Bronze")] = ["Metallic"],
        };
        var items = new ItemFacts
        {
            StuffCategories = metal,
            Market = new Dictionary<Resource, double>
            {
                [Steel] = 1.9, [Plasteel] = 9, [new("Silver")] = 1, [new("Gold")] = 10, [new("Bronze")] = 1.5, [new("Cloth")] = 1.5, [new("Leather")] = 2.1,
            },
        };
        EquatableArray<EquatableArray<Amount>> slots =
        [
            // Silver and gold are small-volume: ten times the units, so they cost more than the steel they would replace.
            [new Amount(Steel, 50), new Amount(Plasteel, 50), new Amount(new("Silver"), 500), new Amount(new("Gold"), 500), new Amount(new("Bronze"), 50)],
            [new Amount(new("Leather"), 10), new Amount(new("Cloth"), 10)],
        ];
        Assert.Equal([new Resource("Bronze"), new Resource("Cloth"), Steel], GearRules.GearFilter(slots, Steel, items));
    }
}
