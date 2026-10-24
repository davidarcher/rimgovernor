using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Every ResearchProjectDef as the project's static facts. The cost, the knowledge category, the
// prerequisites and the required bench are fields of the def, so the catalog carries no second copy of them. The game's
// ResearchProjectDef.hidden is a codex state and not a static fact: every project starts not hidden, and the research
// read marks a codex-hidden one from its "hidden" lock reason.

public sealed partial class DefinitionCatalog
{
    private Dictionary<string, P.ResearchProjectFacts> BuildResearchFacts()
    {
        var facts = new Dictionary<string, P.ResearchProjectFacts>(StringComparer.Ordinal);
        var unlocks = Unlocks();
        foreach (var row in Rows<ResearchProjectDef>().Values)
        {
            var name = row.defName;
            if (string.IsNullOrEmpty(name))
            {
                throw new CatalogException("invalid catalog research project");
            }

            var cost = ResearchProjectCost(row);
            var category = row.knowledgeCategory?.defName ?? string.Empty;
            if (category.Length > 0 && (!double.IsFinite(cost) || cost <= 0))
            {
                throw new CatalogException($"catalog knowledge project {name} lacks a finite cost");
            }

            facts.Add(name, new P.ResearchProjectFacts
            {
                KnowledgeCategory = category,
                Cost = cost,
                TechLevel = (RimGovernor.Contracts.Wire.Defs.TechLevel)(int)row.techLevel, // RimWorld.TechLevel, value for value
                Prerequisites = ProjectIds(row.prerequisites),
                HiddenPrerequisites = ProjectIds(row.hiddenPrerequisites),
                Unlocks = unlocks.TryGetValue(name, out var unlocked) ? new([.. unlocked]) : default,
            });
        }

        return facts;
    }

    // What each project unlocks, gathered as ResearchProjectDef.UnlockedDefs does (confirmed with ilspycmd): the products of
    // every recipe whose researchPrerequisite or researchPrerequisites name it, every ThingDef and TerrainDef whose
    // researchPrerequisites do, every plant whose sowResearchPrerequisites do, every surgery (a recipe one of whose users is a
    // pawn, RecipeDef.IsSurgery over AllRecipeUsers) whose researchPrerequisites do, and every PsychicRitualDef whose
    // researchPrerequisite does. By project defName, the defNames sorted.
    private Dictionary<string, SortedSet<string>> Unlocks()
    {
        var unlocks = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(ResearchProjectDef? project, Def? def)
        {
            if (project is null || def is null)
            {
                return;
            }

            if (!unlocks.TryGetValue(project.defName, out var set))
            {
                unlocks[project.defName] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(def.defName);
        }

        var pawnUsed = _thingDefs.Values.Where(t => t.category == ThingCategory.Pawn).SelectMany(t => t.recipes.Seq()).ToHashSet();
        foreach (var recipe in Rows<RecipeDef>().Values)
        {
            foreach (var project in recipe.researchPrerequisites.Seq().Prepend(recipe.researchPrerequisite))
            {
                foreach (var product in recipe.products.Seq())
                {
                    Add(project, product.thingDef);
                }
            }

            if (pawnUsed.Contains(recipe) || recipe.recipeUsers.Seq().Any(u => u.category == ThingCategory.Pawn))
            {
                foreach (var project in recipe.researchPrerequisites.Seq())
                {
                    Add(project, recipe);
                }
            }
        }

        foreach (var thing in _thingDefs.Values)
        {
            foreach (var project in thing.researchPrerequisites.Seq().Concat(thing.plant?.sowResearchPrerequisites.Seq() ?? []))
            {
                Add(project, thing);
            }
        }

        foreach (var terrain in _terrainDefs.Values)
        {
            foreach (var project in terrain.researchPrerequisites.Seq())
            {
                Add(project, terrain);
            }
        }

        foreach (var ritual in Rows<PsychicRitualDef>().Values)
        {
            Add(ritual.researchPrerequisite, ritual);
        }

        return unlocks;
    }

    // Where a category's knowledge goes while its slot funds no project (ResearchManager.ApplyKnowledge): Advanced into Basic.
    private Dictionary<string, string> BuildKnowledgeOverflow() =>
        Rows<KnowledgeCategoryDef>().Values.ToDictionary(row => row.defName, row => row.overflowCategory?.defName ?? string.Empty, StringComparer.Ordinal);

    private static Domain.EquatableArray<P.ResearchProjectId> ProjectIds(IEnumerable<ResearchProjectDef>? projects) =>
        new([.. projects.Seq().Select(project => new P.ResearchProjectId(project.defName))]);

    /// <summary>ResearchProjectDef.Cost: the base cost, or the knowledge cost of a project with none (a knowledge project has no tech-level factor).</summary>
    public static double ResearchProjectCost(ResearchProjectDef row) => row.baseCost > 0 ? row.baseCost : row.knowledgeCost;
}
