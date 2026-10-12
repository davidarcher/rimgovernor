package bridge

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// A plant's clearance row is its harvestWork and what it yields; a def the
// catalog lacks has none.
func TestClearRowReadsAPlantsHarvestWorkAndYield(t *testing.T) {
	catalog := &DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{
		"Plant_TreeOak": {Plant: &d.PlantProperties{HarvestWork: 200, HarvestYield: 20, HarvestedThingDef: "WoodLog"}},
	}}
	row, ok, err := catalog.ClearRow("Plant_TreeOak", "")
	if err != nil || !ok || row.Work != 200 || len(row.Yield) != 1 || row.Yield[0].Resource != "WoodLog" || row.Yield[0].Count != 20 {
		t.Fatalf("row %+v ok %v err %v", row, ok, err)
	}
	if _, ok, err := catalog.ClearRow("Nothing", ""); ok || err != nil {
		t.Fatalf("a def the catalog lacks has a row: %v %v", ok, err)
	}
}
