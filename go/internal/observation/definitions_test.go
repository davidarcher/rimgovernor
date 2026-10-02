package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestDefinitionsResolveAgainstTheCatalog (#1340): availability is derived
// from the catalog's research prerequisites and the finished projects, a
// crop takes the map's demand and diet rows, and a name the catalog lacks
// is unavailable.
func TestDefinitionsResolveAgainstTheCatalog(t *testing.T) {
	catalog := testCatalog(
		&o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String("Wall")}},
		&o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String("Battery")}, ResearchPrerequisites: []string{"Batteries"}},
		&o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String("Plant_Rice")}, GrowDays: proto.Float64(3)},
	)
	crops := map[string]*o.EdibleCrop{"Plant_Rice": {DefName: proto.String("Plant_Rice"), NutritionDemandPerDay: proto.Float64(4), DietAllowed: proto.Bool(true)}}
	names := []string{"Wall", "Battery", "Plant_Rice", "Missing", "Wall"}
	unknown := definitionFacts{catalog: catalog, crops: crops}.appendDefinitions(nil, names)
	if len(unknown) != 4 || unknown[0].Available != domain.Known(true) || unknown[1].Available != domain.Unknown[bool]() || unknown[3].Available != domain.Known(false) {
		t.Fatalf("without research: %+v", unknown)
	}
	if unknown[2].NutritionDemandPerDay != domain.Known(4.0) || unknown[2].DietAllowed != domain.Known(true) || unknown[0].NutritionDemandPerDay != domain.Unknown[float64]() {
		t.Fatalf("crop facts: %+v", unknown[2])
	}
	for finished, want := range map[string]bool{"": false, "Batteries": true} {
		got := definitionFacts{catalog: catalog, finished: finishedSet([]string{finished})}.resolve("Battery")
		if got.Available != domain.Known(want) {
			t.Fatalf("finished %q: %v", finished, got.Available)
		}
	}
}
