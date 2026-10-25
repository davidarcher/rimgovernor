using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>One source's ingredient contribution to a bench recipe for one pawn, to one accepting storage cell. Native emits
/// the row only when the pawn reaches the source, the bench stage and the destination, so the approach and supply walks are
/// always priced.</summary>
public sealed record CoupledFoodConversion(string Source, string Pawn, string Bench, string Recipe,
    string Ingredient, int IngredientCount, string Product, int ProductCount,
    Cell SourceCell, Cell BenchCell, Cell Destination,
    double ApproachTicks, double SupplyTicks, int CarryingCapacity, int AvailableInput)
{
    /// <summary>Whether the pawn may haul the ingredient: hauling enabled, manipulation, and room for one unit.</summary>
    public required bool CanSupply { get; init; }
    public required bool RequiresHarvest { get; init; }
    public required string WorkGiver { get; init; }
    public required string Bill { get; init; }
    public required FoodCooking Cooking { get; init; }
    public required FoodDelivery Delivery { get; init; }
}

/// <summary>Whether the pawn can cook the recipe at the bench, and how.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Cook), "cook")]
[JsonDerivedType(typeof(Incapable), "incapable")]
public abstract record FoodCooking
{
    private protected FoodCooking() { }
    /// <summary>The recipe's work at the pawn's and bench's speed; the chance its meal is poisoned, by
    /// CompFoodPoisonable.Notify_RecipeProduced: the bench room's FoodPoisonChance stat r (the roomless score without a room),
    /// else the cook's FoodPoisonChance stat c, so r + (1 - r) c (#3167); the skill the work trains, when it trains one.</summary>
    public sealed record Cook(double WorkTicks, double FoodPoisonChance, ProductiveSkillWork? SkillWork) : FoodCooking;
    /// <summary>The pawn cannot cook it: cooking disabled, the recipe's skill unmet, no resolvable job, or no work speed.</summary>
    public sealed record Incapable : FoodCooking;
}

/// <summary>Whether the pawn can carry the product from the bench to the destination, and in how long.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Carried), "carried")]
[JsonDerivedType(typeof(CannotCarry), "cannot_carry")]
[JsonDerivedType(typeof(NoReturnPath), "no_return_path")]
public abstract record FoodDelivery
{
    private protected FoodDelivery() { }
    /// <summary>Every trip's walk to the destination, and the walks back to the bench between trips.</summary>
    public sealed record Carried(double Ticks) : FoodDelivery;
    /// <summary>The pawn cannot haul, or has no room for one unit of the product.</summary>
    public sealed record CannotCarry : FoodDelivery;
    /// <summary>The product takes several trips and native finds no path from the destination back to the bench.</summary>
    public sealed record NoReturnPath : FoodDelivery;
}

/// <summary>How long a fresh unit keeps at an observed cell's temperature before it starts to rot.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Rots), "rots")]
[JsonDerivedType(typeof(Never), "never")]
public abstract record FoodFreshness
{
    private protected FoodFreshness() { }
    public sealed record Rots(double Ticks) : FoodFreshness;
    /// <summary>The resource has no CompRottable, or the temperature does not rot it.</summary>
    public sealed record Never : FoodFreshness;
}

public sealed record CoupledFoodUse(string Pawn, string Resource, Cell Location, bool Eligible,
    double Nutrition, double ChewTicks, int MaxCount, FoodFreshness FreshTicks)
{
    /// <summary>The memories eating a fresh unit of the resource gives the pawn (FoodUtility.ThoughtsFromIngesting on an
    /// unspawned unit), as a food target's MealThoughts (#3598). A cooked meal's unit holds its <see cref="Ingredient"/> (#3872).
    /// Empty for a consumer without a mood.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public required EquatableArray<NeedMemory> Thoughts { get; init; }
    /// <summary>The source ingredient a governor batch cooks this resource from: a conversion's Ingredient, one def per batch
    /// (the recipe step supplies its ingredient stacks), so each batch the forecast can choose has its own row (#3872). Empty for a
    /// resource eaten as it is (a harvest, a carcass's meat, a stocked meal with its own ingredients).</summary>
    public required string Ingredient { get; init; }
}

public sealed record CoupledFoodSource(string Target, string Resource, Cell Location, int Count, FoodFreshness FreshTicks);
