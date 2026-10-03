package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
		bridge.FixtureDef{Name: "Wall"},
		bridge.FixtureDef{Name: "Battery", Research: []string{"Batteries"}},
		bridge.FixtureDef{Name: "Plant_Rice", Plant: &bridge.FixturePlant{GrowDays: 3, SowTags: []string{"Ground"}}},
	)
	crops := map[string]*o.EdibleCrop{"Plant_Rice": {DefName: proto.String("Plant_Rice"), NutritionDemandPerDay: proto.Float64(4), DietAllowed: proto.Bool(true)}}
	names := []string{"Wall", "Battery", "Plant_Rice", "Missing", "Wall"}
	unknown, err := definitionFacts{catalog: catalog, crops: crops}.appendDefinitions(nil, names)
	if err != nil {
		t.Fatal(err)
	}
	if len(unknown) != 4 || unknown[0].Available != domain.Known(true) || unknown[1].Available != domain.Unknown[bool]() || unknown[3].Available != domain.Known(false) {
		t.Fatalf("without research: %+v", unknown)
	}
	if unknown[2].NutritionDemandPerDay != domain.Known(4.0) || unknown[2].DietAllowed != domain.Known(true) || unknown[0].NutritionDemandPerDay != domain.Unknown[float64]() {
		t.Fatalf("crop facts: %+v", unknown[2])
	}
	for finished, want := range map[string]bool{"": false, "Batteries": true} {
		got, err := definitionFacts{catalog: catalog, finished: finishedSet([]string{finished})}.resolve("Battery")
		if err != nil {
			t.Fatal(err)
		}
		if got.Available != domain.Known(want) {
			t.Fatalf("finished %q: %v", finished, got.Available)
		}
	}
}

// A row the view needs and the catalog lacks fails the resolve (#1731): a
// plant whose harvested product the catalog has no row for is no default.
func TestDefinitionsFailWhenARowTheViewNeedsIsMissing(t *testing.T) {
	catalog := testCatalog(bridge.FixtureDef{Name: "Plant_Rice", Plant: &bridge.FixturePlant{GrowDays: 3, SowTags: []string{"Ground"}}})
	wire := catalog.ThingDefs["Plant_Rice"]
	wire.Plant.HarvestedThingDef = "Rice_Missing"
	if _, err := (definitionFacts{catalog: catalog}).resolve("Plant_Rice"); err == nil {
		t.Fatal("a plant harvesting a def with no row resolved")
	}
}
