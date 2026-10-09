package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RecipeLedgerKind is the work ledger's declare-only class of a recipe by what
// it makes (policy.LedgerBillKind): a mech gestation (mechKind is the recipe's
// RecipeMechKind), medicine, a body part item or food a baby can ingest;
// policy.LedgerProduction for every other recipe. A bill of a declare-only
// class is never removed as an orphan in the first pass of the ledger (#2604),
// so the class is read from the catalog row alone and is the same after a
// restart. A nil catalog classifies nothing; a recipe with no row is an error.
func (catalog *DefinitionCatalog) RecipeLedgerKind(name, mechKind string) (policy.LedgerBillKind, error) {
	if mechKind != "" {
		return policy.LedgerMechGestation, nil
	}
	if catalog == nil {
		return policy.LedgerProduction, nil
	}
	row, err := catalog.Recipe(name)
	if err != nil {
		return policy.LedgerProduction, err
	}
	products := row.GetProducts()
	baby := len(products) > 0
	for _, product := range products {
		def := product.GetValue().GetThingDef()
		if def == "" {
			baby = false
			continue
		}
		medicine, err := catalog.Medicine(def)
		if err != nil {
			return policy.LedgerProduction, err
		}
		if medicine {
			return policy.LedgerMedical, nil
		}
		thing, err := catalog.thingRow(def)
		if err != nil {
			return policy.LedgerProduction, err
		}
		within, err := catalog.categoriesWithin(def, thing)
		if err != nil {
			return policy.LedgerProduction, err
		}
		if within[categoryBodyParts] {
			return policy.LedgerSurgery, nil
		}
		baby = baby && thing.GetIngestible().GetBabiesCanIngest()
	}
	if baby {
		return policy.LedgerBabyFood, nil
	}
	return policy.LedgerProduction, nil
}
