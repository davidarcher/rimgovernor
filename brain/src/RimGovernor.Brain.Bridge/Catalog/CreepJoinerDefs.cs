using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go creepjoiner_defs.go: the creepjoiner planner's def lookups. They read the defs and name no def.

public sealed partial class DefinitionCatalog
{
    /// <summary>The recipe defs whose worker is the game's surgical inspection (Recipe_SurgicalInspection); none without Anomaly.</summary>
    public IReadOnlySet<string> SurgicalInspectionRecipes()
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, recipe) in Rows<RecipeDef>())
        {
            if (recipe.workerClass is not null && typeof(Recipe_SurgicalInspection).IsAssignableFrom(recipe.workerClass))
            {
                output.Add(name);
            }
        }
        return output;
    }

    /// <summary>Whether the game runs Anomaly: the defs carry EntityCategoryDef rows only with the expansion.</summary>
    public bool HasAnomaly() => Rows<EntityCategoryDef>().Count > 0;

    /// <summary>What the creepjoiner downside defs add: the trait and hediff def names over every CreepJoinerDownsideDef row. A game without Anomaly adds none.</summary>
    public P.CreepJoinerDownsides CreepJoinerDownsides()
    {
        var traits = new HashSet<string>(StringComparer.Ordinal);
        var hediffs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in Rows<CreepJoinerDownsideDef>().Values)
        {
            foreach (var t in def.traits.Seq())
            {
                traits.Add(t.def.defName);
            }
            foreach (var h in def.hediffs.Seq())
            {
                hediffs.Add(h.defName);
            }
        }
        return new P.CreepJoinerDownsides { Traits = traits, Hediffs = hediffs };
    }
}
