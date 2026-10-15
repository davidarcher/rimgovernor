using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire.Observations;
using RimWorld;
using Verse;

namespace RimGovernor.Brain.Bridge;

public sealed partial class DefinitionCatalog
{
    /// <summary>Native ordinary recipe learning, independent of work speed. Custom workers are unassessed.</summary>
    public RecipeLearning RecipeLearning(string recipe) => ObserveRecipeLearning(Recipe(recipe));

    public static RecipeLearning ObserveRecipeLearning(RecipeDef row)
    {
        var supported = row.Worker.GetType() == typeof(RecipeWorker);
        return new(row.workSkill?.defName ?? "", supported ? Fact.Known(row.workSkill is null ? 0d : 0.1d * row.workSkillLearnFactor) : default,
            row.UsesUnfinishedThing, "Toils_Recipe.DoRecipeWork/FinishRecipeAndStartStoringProduct");
    }
}
