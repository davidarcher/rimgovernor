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
            });
        }

        return facts;
    }

    // Where a category's knowledge goes while its slot funds no project (ResearchManager.ApplyKnowledge): Advanced into Basic.
    private Dictionary<string, string> BuildKnowledgeOverflow() =>
        Rows<KnowledgeCategoryDef>().Values.ToDictionary(row => row.defName, row => row.overflowCategory?.defName ?? string.Empty, StringComparer.Ordinal);

    private static Domain.EquatableArray<P.ResearchProjectId> ProjectIds(IEnumerable<ResearchProjectDef>? projects) =>
        new([.. projects.Seq().Select(project => new P.ResearchProjectId(project.defName))]);

    /// <summary>ResearchProjectDef.Cost: the base cost, or the knowledge cost of a project with none (a knowledge project has no tech-level factor).</summary>
    public static double ResearchProjectCost(ResearchProjectDef row) => row.baseCost > 0 ? row.baseCost : row.knowledgeCost;
}
