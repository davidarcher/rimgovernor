namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The food and book kinds the policy cases assert on. Go classified each allowed definition from the definition catalog
/// (<c>bridge.DefinitionCatalog.FoodKindOf</c>, <c>Books</c>); the catalog rows are not on the wire the harness reads, so
/// the vanilla definitions these cases meet are named here instead. A definition not named is <see cref="Other"/>.
/// </summary>
internal static class PawnKinds
{
    internal const string Other = "other";
    internal const string Kibble = "kibble";
    internal const string MealAwful = "meal_awful";
    internal const string MealSimple = "meal_simple";
    internal const string MealFine = "meal_fine";
    internal const string MealLavish = "meal_lavish";
    internal const string HumanMeat = "human_meat";
    internal const string RawMeat = "raw_meat";
    internal const string InsectMeat = "insect_meat";

    internal const string Textbook = "textbook";
    internal const string Schematic = "schematic";
    internal const string Tome = "tome";
    internal const string Novel = "novel";

    private static readonly string[] InsectMeats = ["Meat_Megaspider", "Meat_Megascarab", "Meat_Spelopede"];

    /// <summary>The kind a food definition has for diets: meals by their tier, kibble, then meat by its source.</summary>
    internal static string Food(string def)
    {
        if (def.StartsWith("MealSimple", StringComparison.Ordinal))
        {
            return MealSimple;
        }

        if (def.StartsWith("MealFine", StringComparison.Ordinal))
        {
            return MealFine;
        }

        if (def.StartsWith("MealLavish", StringComparison.Ordinal))
        {
            return MealLavish;
        }

        if (def is "MealNutrientPaste" or "MealSurvivalPack" or "Pemmican")
        {
            return MealAwful;
        }

        if (def == "Kibble")
        {
            return Kibble;
        }

        if (def == "Meat_Human")
        {
            return HumanMeat;
        }

        if (InsectMeats.Contains(def, StringComparer.Ordinal))
        {
            return InsectMeat;
        }

        return def.StartsWith("Meat_", StringComparison.Ordinal) ? RawMeat : Other;
    }

    /// <summary>The kind a book definition has for reading policies.</summary>
    internal static string Book(string def) => def switch
    {
        "Tome" => Tome,
        "Textbook" => Textbook,
        "Schematic" => Schematic,
        "Novel" => Novel,
        _ => Other,
    };
}
