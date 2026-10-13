using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_drug_facts.go: the drug facts of the item facts.

public sealed partial class DefinitionCatalog
{
    // BuildDrugFacts reads the drug facts of the defs: every def carrying a CompProperties_Drug with a chemical, in
    // preference order (social before hard, then the game's listOrder, then name), each chemical with whether its
    // addiction hediff fades by itself, and the drug whose hediff makes its taker immune to diseases.
    private P.ItemFacts BuildDrugFacts(P.ItemFacts items)
    {
        var drugs = new List<(P.Drug Drug, float Order)>();
        var chemicals = new Dictionary<string, P.Chemical>(StringComparer.Ordinal);
        var prevention = new List<P.Prevention>();
        foreach (var def in _thingDefs.Values.OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            if (def.GetCompProperties<CompProperties_Drug>() is not { } comp)
            {
                continue;
            }

            // A drug with no chemical (penoxycyline) builds no addiction but may still protect against disease.
            if (ImmunityOf(def) is { } immune)
            {
                prevention.Add(immune);
            }

            if (comp.chemical is not { } chemical)
            {
                continue;
            }

            drugs.Add((
                new P.Drug(
                    new P.Resource(def.defName),
                    chemical.defName,
                    def.ingestible?.drugCategory == DrugCategory.Social,
                    comp.isCombatEnhancingDrug),
                comp.listOrder));
            if (!chemicals.ContainsKey(chemical.defName))
            {
                chemicals[chemical.defName] = new P.Chemical(AddictionFades(chemical.defName));
            }
        }

        var ranked = drugs
            .OrderByDescending(r => r.Drug.Social)
            .ThenBy(r => r.Order)
            .ThenBy(r => r.Drug.Def.Value, StringComparer.Ordinal)
            .Select(r => r.Drug);
        return items with
        {
            Drugs = [.. ranked],
            Chemicals = chemicals,
            Prevention = prevention.OrderBy(p => p.Drug.Value, StringComparer.Ordinal).FirstOrDefault(),
        };
    }

    // AddictionFades is whether the chemical's addiction hediff loses severity by itself (a
    // HediffCompProperties_SeverityPerDay below zero), which is what lets a weaning plan end it; a chemical that cannot
    // be addicted has no hediff and none to wean.
    private bool AddictionFades(string chemical)
    {
        var row = RequireRow<ChemicalDef>(chemical);
        return row.addictionHediff is { } hediff
            && hediff.comps.Seq().OfType<HediffCompProperties_SeverityPerDay>().Any(c => c.severityPerDay < 0);
    }

    // ImmunityOf is what a drug does against disease: the hediffs its ingestion gives that make the taker immune
    // (HediffStage.makeImmuneTo) and the days the hediff lasts (its disappearance time, or the given severity over its
    // per-day decay); null for a drug that makes none immune.
    private static P.Prevention? ImmunityOf(ThingDef def)
    {
        var diseases = new List<string>();
        var ticks = 0;
        foreach (var give in def.ingestible?.outcomeDoers.Seq().OfType<IngestionOutcomeDoer_GiveHediff>() ?? [])
        {
            var hediff = give.hediffDef;
            var immune = hediff.stages.Seq().SelectMany(s => s.makeImmuneTo.Seq()).Select(h => h.defName).ToList();
            if (immune.Count == 0)
            {
                continue;
            }

            var severity = give.severity > 0 ? give.severity : hediff.initialSeverity;
            if (hediff.maxSeverity > 0 && severity > hediff.maxSeverity)
            {
                severity = hediff.maxSeverity;
            }

            ticks = 0;
            foreach (var comp in hediff.comps.Seq())
            {
                if (comp is HediffCompProperties_Disappears gone)
                {
                    ticks = gone.disappearsAfterTicks.min;
                }

                // Penoxycyline wears off by severity decay, not a disappear comp.
                if (comp is HediffCompProperties_SeverityPerDay { severityPerDay: < 0 } perDay && severity > 0)
                {
                    ticks = (int)(severity / -(double)perDay.severityPerDay * GameTime.TicksPerDay);
                }
            }

            if (ticks <= 0)
            {
                throw new CatalogException($"hediff {hediff.defName} of {def.defName} makes immune but never disappears");
            }

            diseases.AddRange(immune);
        }

        if (diseases.Count == 0)
        {
            return null;
        }

        return new P.Prevention(
            new P.Resource(def.defName),
            (double)ticks / GameTime.TicksPerDay,
            new([.. diseases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
    }
}
