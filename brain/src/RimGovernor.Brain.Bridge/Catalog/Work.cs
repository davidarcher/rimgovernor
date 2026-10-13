using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_work.go.

public sealed partial class DefinitionCatalog
{
    /// <summary>The skill a work type is scored by: the first of its relevant skills, empty for unskilled work. A work type the catalog lacks is an error.</summary>
    public string WorkTypeSkill(string work) =>
        (DefRow<WorkTypeDef>(work) ?? throw new CatalogException($"catalog has no work type {work}")).relevantSkills.Seq().FirstOrDefault()?.defName ?? string.Empty;

    /// <summary>Sets the work row's Skill and Order from its WorkTypeDef (relevant skill and natural priority).</summary>
    public P.WorkPriority ResolveWorkRow(P.WorkPriority row)
    {
        var name = Wire.Name(row.Work);
        return row with { Skill = WorkTypeSkill(name), Order = DefRow<WorkTypeDef>(name)!.naturalPriority };
    }

    // The policy's WorkType names the work types the vanilla game and the supported DLC define; a work type of another
    // mod has no value there and so no answer here.
    private static bool TryWorkType(string defName, out P.WorkType work) => Wire.TryParse(defName, out work) && work != P.WorkType.None;
}
