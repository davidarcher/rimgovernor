package bridge

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// TestSurgicalInspectionRecipesComeFromTheWorkerClass: the recipes are
// the mirror's RecipeDefs whose worker class is the inspection's, whatever
// they are named; a catalog without recipes or a nil catalog has none.
func TestSurgicalInspectionRecipesComeFromTheWorkerClass(t *testing.T) {
	catalog := &DefinitionCatalog{Defs: map[protoreflect.FullName]map[string]proto.Message{
		(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName(): {
			"Anything":   &d.RecipeDef{DefName: "Anything", WorkerClass: "RimWorld.Recipe_SurgicalInspection"},
			"Short":      &d.RecipeDef{DefName: "Short", WorkerClass: "Recipe_SurgicalInspection"},
			"Lookalike":  &d.RecipeDef{DefName: "Lookalike", WorkerClass: "RimWorld.Recipe_SurgicalInspectionX"},
			"RemovePart": &d.RecipeDef{DefName: "RemovePart", WorkerClass: "RimWorld.Recipe_RemoveBodyPart"},
		},
	}}
	got := catalog.SurgicalInspectionRecipes()
	if len(got) != 2 || !got["Anything"] || !got["Short"] {
		t.Fatal(got)
	}
	if len((&DefinitionCatalog{}).SurgicalInspectionRecipes()) != 0 || len((*DefinitionCatalog)(nil).SurgicalInspectionRecipes()) != 0 {
		t.Fatal("a catalog without recipes has an inspection recipe")
	}
}
