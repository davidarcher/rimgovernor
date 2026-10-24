namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>One recipe's material conversion, shared by the food portfolio and live Method generation.</summary>
internal static class FoodRecipeActivity
{
    internal static ForecastActivity Create(string id, string location, string[] prerequisites,
        Resource ingredient, double required, Resource product, double produced) => new(id, location, location, prerequisites)
        {
            Inputs = [new(ingredient, required)], Outputs = [new(product, produced)]
        };
}
