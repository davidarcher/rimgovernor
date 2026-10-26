using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodStorageOrderTests
{
    private static readonly MealCook[] Cooks = [new() { Pawn = new("cook"), Skill = 6 }];
    private static readonly FoodSupply Supply = new()
    {
        Larder = new() { Corpses = [], RawMeatNutrition = 0, CookDemandNutrition = 0 },
        Stocks = [new() { Id = "raw", DefName = "allowed-raw", Count = 40, Nutrition = 20, RawClass = FoodIngredientClass.Vegetable,
            IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = true, Holder = new(""), Eaters = [],
            Rot = new FoodRot.Rots(GameTime.TicksPerDay), Place = new FoodPlace.Stands(false, ""), TemperatureC = 20,
            RotRate = 1, Form = new FoodForm.Item(), Forbidden = false, UnitMass = .1 }],
    };

    private static Declared Declare(double nutrition, IReadOnlyList<ProductionBench> production, IReadOnlyList<GearBench> benches,
        IReadOnlyList<string> ingredients) => FoodOrdersRules.FoodStorageOrders(nutrition, production, benches, Supply, Cooks, ingredients);
    private static ProductionRecipe Recipe(string name, double nutrition = .5, double work = 20, bool perishable = false,
        bool edible = true, bool available = true, long storable = 100, int skill = 0, FoodIngredientClass ingredient = FoodIngredientClass.Vegetable) => new()
    {
        Access = ProductionBenchFixtures.Recipe(name, available),
        Food = new RecipeFood.Food(0, work, new RecipeCooking.FromRaw(1.5, [new() { Alternatives = [ingredient] }], skill)),
        Products = [new ProductionProduct { Food = new ProductFood.Food(new() { DefName = name + "-food", Edible = edible, Storable = storable },
            FoodKind.MealSimple, nutrition, perishable, false) }],
    };

    private static ProductionBench Bench(params ProductionRecipe[] recipes) =>
        ProductionBenchFixtures.Cooking("native-bench", "native-stove", "token", true) with { Recipes = recipes };

    private static GearBench LedgerBench(ProductionBench bench) => new()
    {
        Observed = bench.Observed, Bills = [], Recipes = EquatableArray.From(bench.Recipes.Select(r => new GearRecipe
        {
            Access = r.Access, Ingredients = [], RequiredWork = [], WorkAmount = 100,
            Products = EquatableArray.From(r.Products.Select(p => new Resource(p.Name))),
        })),
    };

    [Theory]
    [InlineData(25)]
    [InlineData(97)]
    public void NativePreservedCandidatesReachComparisonAndActualLedgerBill(int count)
    {
        var recipes = Enumerable.Range(0, count).Select(i => Recipe("preserve-" + i, work: count - i)).ToArray();
        var bench = Bench(recipes);
        var gear = LedgerBench(bench);
        var order = Assert.Single(Declare(3.1, [bench], [gear], ["allowed-raw"]).Orders);
        Assert.Equal(recipes[^1].Name, order.Recipe);
        Assert.Equal(new Resource(recipes[^1].Products[0].Name), order.Product);
        Assert.Equal(7, order.Target);
        Assert.Equal(["allowed-raw"], order.Ingredients);
        var placement = WorkRules.PlaceLedgerOrders([order], [gear]);
        Assert.Empty(placement.Unplaced);
        var bill = AdminReview.LedgerBill(Assert.Single(placement.Placed));
        Assert.Equal(bench.Id, bill.Bench);
        Assert.Equal(order.Recipe, bill.Recipe);
        Assert.Equal(order.Target, bill.Target);
        Assert.Equal(BillMode.FoodTarget, bill.Mode);
    }

    [Fact]
    public void ResearchHostConsumerStorageAndMechanicsGatePreservation()
    {
        var valid = Recipe("supported", storable: 2);
        var bench = Bench(Recipe("perishable", work: 0, perishable: true), Recipe("inedible", work: 0, edible: false),
            Recipe("research", work: 0, available: false), Recipe("no-space", work: 0, storable: 0),
            Recipe("no-host", work: 0) with { Access = ProductionBenchFixtures.Recipe("no-host", onBench: false) },
            Recipe("unknown-mechanism", work: 0) with { Food = new RecipeFood.NotFood() }, valid);
        var selected = Assert.Single(Declare(10, [bench], [], []).Orders);
        Assert.Equal(valid.Name, selected.Recipe);
        Assert.Equal(2, selected.Target);
        Assert.Empty(Declare(10, [bench with { Recipes = bench.Recipes.Where(r => r != valid).ToList() }], [], []).Orders);
        Assert.Empty(Declare(10, [Bench(valid) with { Use = new ProductionUse.Cooking(new() { Usable = false, Recipes = [], Production = [] }) }], [], []).Orders);
    }

    [Fact]
    public void CookSkillAndObservedIngredientsSelectTheSupportedRecipe()
    {
        var bench = Bench(Recipe("needs-skill", work: 0, skill: 7),
            Recipe("needs-protein", work: 0, ingredient: FoodIngredientClass.Meat), Recipe("supported"));
        Assert.Equal("supported", Assert.Single(Declare(10, [bench], [], []).Orders).Recipe);
        Assert.Empty(FoodOrdersRules.FoodStorageOrders(10, [bench], [], Supply, [], []).Orders);
        Assert.Empty(FoodOrdersRules.FoodStorageOrders(10, [bench], [], Supply with { Stocks = [] }, Cooks, []).Orders);
        Assert.Empty(FoodOrdersRules.FoodStorageOrders(10, [bench], [], Supply with { Reserved = new HashSet<string>(StringComparer.Ordinal) { "raw" } }, Cooks, []).Orders);
    }

    [Fact]
    public void AdequateStandingPreservationKeepsItsTargetAndNativeProductContext()
    {
        var bench = Bench(Recipe("preserve"), Recipe("fresh", perishable: true));
        var wanted = Assert.Single(Declare(3, [bench], [], []).Orders);
        var standing = wanted with { Target = 20, Product = default };
        var fresh = standing with { Recipe = "fresh" };
        var gear = LedgerBench(bench) with { Bills = [
            new() { Observed = BillWire.Bill("preserved-bill"), Spec = standing },
            new() { Observed = BillWire.Bill("fresh-bill"), Spec = fresh }] };
        var selected = Assert.Single(Declare(3, [bench], [gear], []).Orders);
        Assert.Equal(20, selected.Target);
        Assert.Equal(wanted.Product, selected.Product);
        Assert.Equal(standing, Assert.Single(FoodOrdersRules.StandingStorageOrders([bench], [gear]).Orders));
    }

    [Fact]
    public void NutritionAndWorkAreRequiredFactsAndExistingStorageNeedsNoProduction()
    {
        Assert.Throws<RequiredFactException>(() => Declare(double.NaN, [], [], []));
        Assert.Throws<RequiredFactException>(() => Declare(10, [Bench(Recipe("broken", work: double.NaN))], [], []));
        Assert.Throws<RequiredFactException>(() => Declare(10, [Bench(Recipe("broken-yield", nutrition: 0))], [], []));
        var review = new FoodStorageReview { Active = true, Deficit = 10 };
        Assert.Equal(FoodStorageMethodKind.Stuck, FoodStorageUpkeepRules.SelectFoodStorageMethod(review, [new() { Id = "raw", Capacity = 1 }]));
        Assert.Equal(FoodStorageMethodKind.Produce, FoodStorageUpkeepRules.SelectFoodStorageMethod(review, [new() { Id = "raw", Capacity = 0 }]));
        Assert.Equal(FoodStorageMethodKind.Recovered, FoodStorageUpkeepRules.SelectFoodStorageMethod(review with { Active = false }, []));
        Assert.Throws<DomainException>(() => FoodStorageUpkeepRules.SelectFoodStorageMethod(review,
            [new() { Id = "raw", Capacity = 1 }, new() { Id = "raw", Capacity = 0 }]));
    }
}
