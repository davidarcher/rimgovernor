namespace RimGovernor.Brain.Policy;

/// <summary>
/// Every slot is required; alternatives within a slot are interchangeable. A fine
/// meal needs (meat OR animalProduct) AND vegetable, not all three.
/// </summary>
public enum FoodIngredientClass
{
    [WireName("")]
    None = 0,
    Meat,
    Vegetable,

    [WireName("animalProduct")]
    AnimalProduct,
    Any,
}

public sealed record FoodIngredientSlot
{
    public IReadOnlyList<FoodIngredientClass> Alternatives { get; init; } = [];
}

/// <summary>What a recipe is worth as food, read from its RecipeDef and product def (the catalog's RecipeFood).</summary>
public abstract record RecipeFood
{
    private RecipeFood()
    {
    }

    /// <summary>The recipe makes no single nutrition-giving ingestible: no product, several, or one with no nutrition.</summary>
    public sealed record NotFood : RecipeFood;

    /// <summary>
    /// The recipe makes one nutrition-giving ingestible. Mood is its taste thought's base mood effect (0 with no taste
    /// thought) and WorkPerNutrition the recipe's work over the nutrition one bill iteration makes.
    /// </summary>
    public sealed record Food(double Mood, double WorkPerNutrition, RecipeCooking Cooking) : RecipeFood;
}

/// <summary>How a food recipe cooks from raw food: its nutrition-valued ingredient slots as raw food classes.</summary>
public abstract record RecipeCooking
{
    private RecipeCooking()
    {
    }

    /// <summary>
    /// The recipe is not cooked from raw food classes: it values its ingredients by something other than nutrition, has
    /// none, or a slot takes a def that is no ingestible or of no raw class.
    /// </summary>
    public sealed record NotFromRaw : RecipeCooking;

    /// <summary>
    /// Cooked from raw food: NutrientEfficiency is the product nutrition over the slots' summed base nutrition,
    /// IngredientClasses the raw class alternatives of each slot, and CookSkillFloor the highest minimum level the
    /// recipe's skill requirements set on its work skill (0 when none does).
    /// </summary>
    public sealed record FromRaw(double NutrientEfficiency, EquatableArray<FoodIngredientSlot> IngredientClasses, int CookSkillFloor) : RecipeCooking;
}
