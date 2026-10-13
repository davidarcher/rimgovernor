namespace RimGovernor.Brain.Policy;

/// <summary>
/// Ports <c>meal_recipe.go</c>. Every slot is required; alternatives within a slot are interchangeable. A fine
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
