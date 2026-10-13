using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Sums the nutrition target already committed to existing TargetCount bills. It stays distinct from
    /// current edible stock (food_forecast.go), animal feed demand (animal_upkeep.go) and future crop yield
    /// (crop_choice.go, field_capacity.go): a bill still filling toward its target reserves that nutrition
    /// even though none of it is edible yet, so it must never be added to any of those, only netted against
    /// new bill demand. Ok is false when the total is not a usable number.
    /// </summary>
    public static (double Total, bool Ok) ReservedFoodNutrition(IReadOnlyList<ProductionBench> benches)
    {
        var total = 0.0;
        foreach (var bench in benches)
        {
            if (bench.Butcher || bench.Bills.Count > 15)
            {
                continue;
            }
            var nutrition = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Products.Count != 1)
                {
                    continue;
                }
                var product = recipe.Products[0];
                var ek = product.Edible.TryGetValue(out var edible);
                var nk = product.Nutrition.TryGetValue(out var value);
                if (ek && edible && nk && FieldPositive(value))
                {
                    nutrition[recipe.Name] = value;
                }
            }
            foreach (var bill in bench.Bills)
            {
                var fk = bill.Forever.TryGetValue(out var forever);
                var tk = bill.TargetCount.TryGetValue(out var target);
                var known = nutrition.TryGetValue(bill.Recipe, out var value);
                if (!fk || forever || !tk || target <= 0 || !known)
                {
                    continue;
                }
                total += target * value;
            }
        }
        if (!FoodNumber(total))
        {
            return (0, false);
        }
        return (total, true);
    }
}
