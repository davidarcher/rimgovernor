package bridge

import (
	"cmp"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// buildDrugFacts reads the drug facts of the def rows (#1734): every def
// carrying a CompProperties_Drug with a chemical, in preference order
// (social before hard, then the game's listOrder, then name), each chemical
// with whether its addiction hediff fades by itself, and the drug whose
// hediff makes its taker immune to diseases. A row a drug names that the
// catalog lacks is a contract error.
func buildDrugFacts(catalog *DefinitionCatalog, items *policy.ItemFacts) error {
	type ranked struct {
		drug  policy.Drug
		order float32
	}
	var drugs []ranked
	chemicals := map[string]policy.Chemical{}
	var prevention []policy.Prevention
	for name, def := range catalog.ThingDefs {
		comp := compOf(def, (*d.CompPropertiesAny).GetCompProperties_Drug)
		if comp == nil {
			continue
		}
		// A drug with no chemical (penoxycyline) builds no addiction but may
		// still protect against disease.
		immune, err := immunityOf(catalog, name, def)
		if err != nil {
			return err
		}
		if immune != nil {
			prevention = append(prevention, *immune)
		}
		chemical := comp.GetChemical()
		if chemical == "" {
			continue
		}
		drugs = append(drugs, ranked{policy.Drug{
			Def: policy.Resource(name), Chemical: chemical,
			Social: def.GetIngestible().GetDrugCategory() == d.DrugCategory_DRUG_CATEGORY_SOCIAL,
			Combat: comp.GetIsCombatEnhancingDrug(),
		}, comp.GetListOrder()})
		if _, done := chemicals[chemical]; !done {
			weanable, err := addictionFades(catalog, chemical)
			if err != nil {
				return err
			}
			chemicals[chemical] = policy.Chemical{Weanable: weanable}
		}
	}
	slices.SortFunc(drugs, func(a, b ranked) int {
		if a.drug.Social != b.drug.Social {
			if a.drug.Social {
				return -1
			}
			return 1
		}
		return cmp.Or(cmp.Compare(a.order, b.order), cmp.Compare(a.drug.Def, b.drug.Def))
	})
	for _, r := range drugs {
		items.Drugs = append(items.Drugs, r.drug)
	}
	items.Chemicals = chemicals
	if len(prevention) > 0 {
		slices.SortFunc(prevention, func(a, b policy.Prevention) int { return cmp.Compare(a.Drug, b.Drug) })
		items.Prevention = &prevention[0]
	}
	return nil
}

// addictionFades is whether the chemical's addiction hediff loses severity
// by itself (a HediffCompProperties_SeverityPerDay below zero), which is
// what lets a weaning plan end it; a chemical that cannot be addicted has no
// hediff and none to wean.
func addictionFades(catalog *DefinitionCatalog, chemical string) (bool, error) {
	row := DefRow[*d.ChemicalDef](catalog, chemical)
	if row == nil {
		return false, contract("catalog has no chemical %s", chemical)
	}
	if row.GetAddictionHediff() == "" {
		return false, nil
	}
	hediff := DefRow[*d.HediffDef](catalog, row.GetAddictionHediff())
	if hediff == nil {
		return false, contract("catalog has no hediff %s (the addiction of %s)", row.GetAddictionHediff(), chemical)
	}
	for _, comp := range hediff.GetComps() {
		if perDay := comp.GetValue().GetHediffCompProperties_SeverityPerDay(); perDay != nil && perDay.GetSeverityPerDay() < 0 {
			return true, nil
		}
	}
	return false, nil
}

// immunityOf is what a drug does against disease: the hediffs its
// ingestion gives that make the taker immune (HediffStage.makeImmuneTo) and
// the days the hediff lasts (its disappearance time); nil for a drug that
// makes none immune.
func immunityOf(catalog *DefinitionCatalog, name string, def *d.ThingDef) (*policy.Prevention, error) {
	var diseases []string
	var ticks int32
	for _, doer := range def.GetIngestible().GetOutcomeDoers() {
		give := doer.GetValue().GetIngestionOutcomeDoer_GiveHediff()
		if give == nil {
			continue
		}
		hediff := DefRow[*d.HediffDef](catalog, give.GetHediffDef())
		if hediff == nil {
			return nil, contract("catalog has no hediff %s (given by %s)", give.GetHediffDef(), name)
		}
		var immune []string
		for _, stage := range hediff.GetStages() {
			immune = append(immune, stage.GetValue().GetMakeImmuneTo()...)
		}
		if len(immune) == 0 {
			continue
		}
		for _, comp := range hediff.GetComps() {
			if gone := comp.GetValue().GetHediffCompProperties_Disappears(); gone != nil {
				ticks = gone.GetDisappearsAfterTicks().GetMin()
			}
		}
		if ticks <= 0 {
			return nil, contract("hediff %s of %s makes immune but never disappears", give.GetHediffDef(), name)
		}
		diseases = append(diseases, immune...)
	}
	if len(diseases) == 0 {
		return nil, nil
	}
	slices.Sort(diseases)
	return &policy.Prevention{Drug: policy.Resource(name), Days: float64(ticks) / float64(domain.TicksPerDay), Diseases: slices.Compact(diseases)}, nil
}
