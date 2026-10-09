package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TestRecipeLedgerKindComesFromTheRows: a recipe is declare-only by what it
// makes, from the catalog alone: medicine, a body part item and food a baby can
// ingest; a mech gestation by its mech kind. Any other recipe is production.
func TestRecipeLedgerKindComesFromTheRows(t *testing.T) {
	catalog := recipeFixture()
	recipes := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	for name, thing := range map[string]string{"Make_Herbal": "MedicineHerbal", "Make_Prosthetic": "SimpleProstheticLeg", "Make_BabyFood": "BabyFood", "Make_Meal": "MealSimple"} {
		recipes[name] = &d.RecipeDef{DefName: name, Products: []*d.Opt_ThingDefCountClass{product(thing, 1)}}
	}
	categories := catalog.Defs[(&d.ThingCategoryDef{}).ProtoReflect().Descriptor().FullName()]
	categories["BodyParts"] = &d.ThingCategoryDef{DefName: "BodyParts", Parent: "Root"}
	categories["BodyPartsSimple"] = &d.ThingCategoryDef{DefName: "BodyPartsSimple", Parent: "BodyParts"}
	catalog.ThingDefs["MedicineHerbal"] = &d.ThingDef{DefName: "MedicineHerbal"}
	catalog.ThingDefs["SimpleProstheticLeg"] = &d.ThingDef{DefName: "SimpleProstheticLeg", ThingCategories: []string{"BodyPartsSimple"}}
	catalog.ThingDefs["BabyFood"] = &d.ThingDef{DefName: "BabyFood", Ingestible: &d.IngestibleProperties{BabiesCanIngest: true}}
	catalog.thingFacts["MedicineHerbal"] = &o.ThingDefFacts{DefName: "MedicineHerbal", Medicine: true}
	catalog.thingFacts["SimpleProstheticLeg"] = &o.ThingDefFacts{DefName: "SimpleProstheticLeg"}
	catalog.thingFacts["BabyFood"] = &o.ThingDefFacts{DefName: "BabyFood"}
	for recipe, want := range map[string]policy.LedgerBillKind{
		"Make_Herbal":     policy.LedgerMedical,
		"Make_Prosthetic": policy.LedgerSurgery,
		"Make_BabyFood":   policy.LedgerBabyFood,
		"Make_Meal":       policy.LedgerProduction,
		"Make_Steel":      policy.LedgerProduction,
		"CremateCorpse":   policy.LedgerProduction,
	} {
		got, err := catalog.RecipeLedgerKind(recipe, "")
		if err != nil || got != want {
			t.Errorf("%s: kind %q, %v; want %q", recipe, got, err, want)
		}
	}
	if got, err := catalog.RecipeLedgerKind("Make_Steel", "Mech_Lifter"); err != nil || got != policy.LedgerMechGestation {
		t.Errorf("a gestation recipe: kind %q, %v", got, err)
	}
	if _, err := catalog.RecipeLedgerKind("Make_Nothing", ""); err == nil {
		t.Error("a recipe with no row is an error")
	}
}
