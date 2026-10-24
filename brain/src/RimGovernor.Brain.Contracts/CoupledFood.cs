using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record CoupledFoodConversion(string Source, string Pawn, string Bench, string Recipe,
    string Ingredient, int IngredientCount, string Product, int ProductCount,
    Cell SourceCell, Cell BenchCell, Cell Destination,
    Fact<double> ApproachTicks, Fact<double> SupplyTicks, Fact<double> WorkTicks, Fact<double> DeliveryTicks,
    int CarryingCapacity, int AvailableInput)
{
    public bool CanSupply { get; init; } = true;
    public bool CanCook { get; init; } = true;
    public bool CanDeliver { get; init; } = true;
    public bool RequiresHarvest { get; init; } = true;
    public string WorkGiver { get; init; } = ""; public string Bill { get; init; } = "";
    public ProductiveSkillWork? SkillWork { get; init; }
    /// <summary>The chance this pawn's meal from this bench is poisoned, by CompFoodPoisonable.Notify_RecipeProduced: the bench
    /// room's FoodPoisonChance stat r (the roomless score without a room), else the cook's FoodPoisonChance stat c, so
    /// r + (1 - r) c. Unknown for a pawn that cannot cook (#3167).</summary>
    public Fact<double> FoodPoisonChance { get; init; }
}
public sealed record CoupledFoodUse(string Pawn, string Resource, Cell Location, Fact<bool> Eligible,
    Fact<double> Nutrition, Fact<double> ChewTicks, int MaxCount, Fact<double> FreshTicks)
{
    /// <summary>The memories eating a fresh unit of the resource gives the pawn (FoodUtility.ThoughtsFromIngesting on an
    /// unspawned unit), as a food target's MealThoughts (#3598). A cooked meal's unit holds its <see cref="Ingredient"/> (#3872).
    /// Empty for a consumer without a mood.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public required EquatableArray<NeedMemory> Thoughts { get; init; }
    /// <summary>The source ingredient a governor batch cooks this resource from: a conversion's Ingredient, one def per batch
    /// (the recipe step supplies its ingredient stacks), so each batch the forecast can choose has its own row (#3872). Empty for a
    /// resource eaten as it is (a harvest, a carcass's meat, a stocked meal with its own ingredients).</summary>
    public string Ingredient { get; init; } = "";
}


public sealed record CoupledFoodSource(string Target, string Resource, Cell Location, int Count, Fact<double> FreshTicks);
