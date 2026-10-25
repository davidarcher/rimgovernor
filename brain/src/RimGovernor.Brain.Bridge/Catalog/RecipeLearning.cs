using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimWorld;
using Verse;

namespace RimGovernor.Brain.Bridge;

public sealed partial class DefinitionCatalog
{
    /// <summary>Native ordinary recipe learning, independent of work speed. <c>Toils_Recipe</c> pays 0.1 × workSkillLearnFactor
    /// raw XP per tick spent on recipe work (<c>ticksSpentDoingRecipeWork</c>, not work amount): during work for unfinished
    /// recipes, otherwise once in <c>FinishRecipeAndStartStoringProduct</c>. Surgery pays there too, before
    /// <c>Bill_Medical.Notify_IterationCompleted</c> applies the worker, so a failed surgery still pays (#3632).
    /// The base worker and the game's own surgery workers (none touches learning or the job) are qualified;
    /// any other worker is not modelled: null, so its work carries no learning. Literal: kept-constants.md, Medicine learning.</summary>
    public static RecipeLearning? ObserveRecipeLearning(RecipeDef row)
    {
        var worker = row.Worker.GetType();
        var supported = worker == typeof(RecipeWorker)
            || typeof(Recipe_Surgery).IsAssignableFrom(worker) && worker.Assembly == typeof(Recipe_Surgery).Assembly;
        return !supported ? null : new(row.workSkill?.defName ?? "", row.workSkill is null ? 0d : 0.1d * row.workSkillLearnFactor,
            row.UsesUnfinishedThing);
    }

    /// <summary>Native raw Medicine XP of one completed tend (#3632): <c>Toils_Tend.FinalizeTend</c> pays 500 for a patient that
    /// is not an animal and 175 for an animal, times the medicine's <c>MedicineTendXpGainFactor</c>, or 0.5 without medicine,
    /// through <c>Pawn_SkillTracker.Learn</c>. Literals: kept-constants.md, Medicine learning.</summary>
    public static double ObserveTendLearning(bool animalPatient, ThingDef? medicine)
        => (animalPatient ? 175d : 500d) * (medicine?.MedicineTendXpGainFactor ?? 0.5f);
}
