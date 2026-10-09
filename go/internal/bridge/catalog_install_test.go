package bridge

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"google.golang.org/protobuf/proto"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// TestMaterialInstallsComeFromTheRows: a part made straight from a stuff
// is the install whose hediff gives back, on removal, the one def the recipe
// consumes; a bionic part (a body part item) and a denture (no stuff) are not.
func TestMaterialInstallsComeFromTheRows(t *testing.T) {
	catalog := recipeFixture()
	recipes := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	only := func(def string, count float32) *d.Opt_IngredientCount {
		return slot(&d.ThingFilter{ThingDefs: []string{def}}, count)
	}
	recipes["InstallPegLeg"] = &d.RecipeDef{DefName: "InstallPegLeg", WorkAmount: 1500, AddsHediff: "PegLeg", AppliedOnFixedBodyParts: []string{"Leg"}, Ingredients: []*d.Opt_IngredientCount{only("WoodLog", 1)}}
	recipes["InstallBionicArm"] = &d.RecipeDef{DefName: "InstallBionicArm", AddsHediff: "BionicArm", AppliedOnFixedBodyParts: []string{"Arm"}, Ingredients: []*d.Opt_IngredientCount{only("BionicArm", 1)}}
	recipes["InstallDenture"] = &d.RecipeDef{DefName: "InstallDenture", AddsHediff: "Denture", AppliedOnFixedBodyParts: []string{"Jaw"}}
	hediffs := map[string]proto.Message{
		"PegLeg":    &d.HediffDef{DefName: "PegLeg", SpawnThingOnRemoved: "WoodLog"},
		"BionicArm": &d.HediffDef{DefName: "BionicArm", SpawnThingOnRemoved: "BionicArm"},
		"Denture":   &d.HediffDef{DefName: "Denture"},
	}
	catalog.Defs[(&d.HediffDef{}).ProtoReflect().Descriptor().FullName()] = hediffs
	categories := catalog.Defs[(&d.ThingCategoryDef{}).ProtoReflect().Descriptor().FullName()]
	categories["BodyParts"] = &d.ThingCategoryDef{DefName: "BodyParts", Parent: "Root"}
	catalog.ThingDefs["BionicArm"] = &d.ThingDef{DefName: "BionicArm", ThingCategories: []string{"BodyParts"}}
	catalog.ThingDefs["WoodLog"] = &d.ThingDef{DefName: "WoodLog"}
	catalog.statValues = &statTable{things: map[defStuff]*statRow{{"WoodLog", ""}: {values: map[string]float32{"MarketValue": 1.2}}}}
	facts, err := catalog.RecipeFacts()
	if err != nil {
		t.Fatal(err)
	}
	want := policy.MaterialInstall{Recipe: "InstallPegLeg", Bodies: []string{"Leg"}, Part: "PegLeg", Work: 1500, Material: "WoodLog", Value: float64(float32(1.2))}
	if len(facts.MaterialInstalls) != 1 || !reflect.DeepEqual(facts.MaterialInstalls[0], want) {
		t.Fatalf("material installs %+v, want just %+v", facts.MaterialInstalls, want)
	}
}

// TestInstallItemComesFromTheRecipeRow: an install recipe's item is the
// body part its ingredient names, natural or artificial; a peg leg made of
// logs installs no item, and a recipe the catalog lacks is an error.
func TestInstallItemComesFromTheRecipeRow(t *testing.T) {
	catalog := recipeFixture()
	recipes := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	only := func(def string) *d.Opt_IngredientCount {
		return slot(&d.ThingFilter{ThingDefs: []string{def}}, 1)
	}
	for name, ingredient := range map[string]string{"InstallBionicArm": "BionicArm", "InstallNaturalKidney": "Kidney", "InstallPegLeg": "WoodLog"} {
		recipes[name] = &d.RecipeDef{DefName: name, Ingredients: []*d.Opt_IngredientCount{only(ingredient), slot(&d.ThingFilter{Categories: []string{"Medicine"}}, 1)}}
	}
	categories := catalog.Defs[(&d.ThingCategoryDef{}).ProtoReflect().Descriptor().FullName()]
	for name, parent := range map[string]string{"BodyParts": "Root", "BodyPartsNatural": "BodyParts", "BodyPartsBionic": "BodyParts", "Medicine": "Root", "Wood": "Root"} {
		categories[name] = &d.ThingCategoryDef{DefName: name, Parent: parent}
	}
	catalog.ThingDefs["BionicArm"] = &d.ThingDef{DefName: "BionicArm", ThingCategories: []string{"BodyPartsBionic"}}
	catalog.ThingDefs["Kidney"] = &d.ThingDef{DefName: "Kidney", ThingCategories: []string{"BodyPartsNatural"}}
	catalog.ThingDefs["WoodLog"] = &d.ThingDef{DefName: "WoodLog", ThingCategories: []string{"Wood"}}
	for recipe, want := range map[string]string{"InstallBionicArm": "BionicArm", "InstallNaturalKidney": "Kidney", "InstallPegLeg": "", "CremateCorpse": ""} {
		item, err := catalog.InstallItem(recipe)
		if err != nil || string(item) != want {
			t.Errorf("InstallItem(%s) = %q, %v; want %q", recipe, item, err, want)
		}
	}
	if _, err := catalog.InstallItem("InstallNothing"); err == nil {
		t.Error("a recipe with no row is an error")
	}
}
