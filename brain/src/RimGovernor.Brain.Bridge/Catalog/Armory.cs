using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The armory ladder.

public sealed partial class DefinitionCatalog
{
    // The armory ladder rung of a TechLevel: the research a recipe or its work table needs is neolithic, medieval
    // (Smithing), industrial (Machining) or spacer (Fabrication). A level off the ladder (undefined, as the Anomaly
    // projects state, or ultra and archotech) has no rung.
    private static P.ArmoryTier? ArmoryTechTier(TechLevel level) => level switch
    {
        TechLevel.Animal or TechLevel.Neolithic => P.ArmoryTier.Neolithic,
        TechLevel.Medieval => P.ArmoryTier.Smithing,
        TechLevel.Industrial => P.ArmoryTier.Machining,
        TechLevel.Spacer => P.ArmoryTier.Fabrication,
        _ => null,
    };

    // The highest rung of the research projects' tech levels, neolithic for none; null when a project is off the ladder.
    private P.ArmoryTier? ResearchArmoryTier(IEnumerable<ResearchProjectDef?> projects)
    {
        var tier = P.ArmoryTier.Neolithic;
        foreach (var project in projects)
        {
            if (project is null)
            {
                continue;
            }

            if (ArmoryTechTier(RequireRow<ResearchProjectDef>(project.defName).techLevel) is not { } rung)
            {
                return null;
            }

            tier = (P.ArmoryTier)Math.Max((int)tier, (int)rung);
        }

        return tier;
    }

    // Whether def is an ordinary weapon the armory may craft (P.WeaponDef.Armament).
    private bool ArmamentProduct(string def) =>
        CategoriesWithin(ThingRow(def)).Contains(CategoryWeapons) && WeaponOf(def).Armament();

    /// <summary>
    /// The armory ladder rung of a recipe made at a work table, or <see cref="P.ArmoryRung.Unladdered"/> when the ladder
    /// does not model it: the highest of the tech levels of the recipe's research prerequisites and of the work table's, so
    /// a recipe needing no research still sits on the rung of the smithy that hosts it. Recipes that make no ordinary weapon,
    /// are gated by an ideology precept, or need research or a table off the tech ladder are unladdered.
    /// </summary>
    public P.ArmoryRung RecipeArmoryTier(string recipe, string bench)
    {
        var row = Recipe(recipe);
        if (row.memePrerequisitesAny.Seq().Any() || row.factionPrerequisiteTags.Seq().Any() || row.fromIdeoBuildingPreceptOnly)
        {
            return new P.ArmoryRung.Unladdered();
        }

        if (!RecipeProductDefs(row).Any(product => ArmamentProduct(product.Value)))
        {
            return new P.ArmoryRung.Unladdered();
        }

        if (ResearchArmoryTier(row.researchPrerequisites.Seq().Prepend(row.researchPrerequisite)) is not { } tier
            || ResearchArmoryTier(ThingRow(bench).researchPrerequisites.Seq()) is not { } tableTier)
        {
            return new P.ArmoryRung.Unladdered();
        }

        return new P.ArmoryRung.Laddered((P.ArmoryTier)Math.Max((int)tier, (int)tableTier));
    }
}
