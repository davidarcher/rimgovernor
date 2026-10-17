using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The drug facts of the item facts.

public sealed partial class DefinitionCatalog
{
    // BuildDrugFacts reads the drug facts of the defs: every def carrying a CompProperties_Drug with a chemical, in
    // preference order (social before hard, then the game's listOrder, then name).
    private P.ItemFacts BuildDrugFacts(P.ItemFacts items)
    {
        var drugs = new List<(P.Drug Drug, float Order)>();
        foreach (var def in _thingDefs.Values.OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            if (def.GetCompProperties<CompProperties_Drug>() is not { chemical: not null } comp)
            {
                continue;
            }

            drugs.Add((
                new P.Drug(
                    new P.Resource(def.defName),
                    def.ingestible?.drugCategory == DrugCategory.Social,
                    comp.isCombatEnhancingDrug),
                comp.listOrder));
        }

        var ranked = drugs
            .OrderByDescending(r => r.Drug.Social)
            .ThenBy(r => r.Order)
            .ThenBy(r => r.Drug.Def.Value, StringComparer.Ordinal)
            .Select(r => r.Drug);
        return items with { Drugs = [.. ranked] };
    }
}
