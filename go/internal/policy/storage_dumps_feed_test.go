package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The rotten dump is the crematorium's feed (#1812): with a crematorium
// built at the south end of the shelter the rotten dump is sited on the open
// ground beside it, while the corpse dump keeps to the general store.
func TestRottenDumpSitedBesideTheCrematorium(t *testing.T) {
	r := stockpileCreateRequest()
	b, err := domain.NewBuilding(CrematoriumDefinition, domain.Cell{X: 7, Z: 17}, domain.North, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	built := []CurrentBuilding{{ID: "ElectricCrematorium_1", Building: b, Cells: []domain.Cell{{X: 7, Z: 17}, {X: 8, Z: 17}, {X: 9, Z: 17}}}}
	feed, ok := CrematoriumFeedAnchor(built)
	if !ok || feed != (domain.Cell{X: 7, Z: 17}) {
		t.Fatalf("feed anchor %v %v", feed, ok)
	}
	storage := StorageRequest{Bounds: r.Bounds, Cells: r.Cells, Protected: r.Protected, Zones: r.Zones}
	storage.Dumps = &DumpStore{
		Needs:   map[string]int{domain.RottenDumpRole: 2, domain.CorpseDumpRole: 1},
		Rooms:   []Room{},
		Anchor:  r.Anchor,
		Anchors: map[string]domain.Cell{domain.RottenDumpRole: feed},
	}
	r.Sited = PlanStorage(storage).Sites
	got := map[string]StockpileEdit{}
	for _, e := range PlanStockpileMaintenance(r).Edits {
		got[e.Role] = e
	}
	rotten, corpse := got[domain.RottenDumpRole], got[domain.CorpseDumpRole]
	if len(rotten.Cells) != 4 || len(corpse.Cells) != 4 {
		t.Fatalf("creates %+v", got)
	}
	for _, c := range rotten.Cells {
		if c.X < 10 || c.Z < 15 {
			t.Fatalf("rotten dump not beside the crematorium: %v", rotten.Cells)
		}
	}
	for _, c := range corpse.Cells {
		if c.Z > 8 {
			t.Fatalf("corpse dump left the general store: %v", corpse.Cells)
		}
	}
	if _, ok := CrematoriumFeedAnchor(nil); ok {
		t.Fatal("no crematorium, no anchor")
	}
}

// Only a rotting animal corpse waits for the rotten dump; a fresh one waits
// for the freezer, and a human one for the corpse dump.
func TestDumpNeedsRoutesCorpsesByKindAndRot(t *testing.T) {
	facts := RoutineFacts{Waste: domain.Known([]WasteItem{
		{Kind: "corpse", CorpseOf: domain.CorpseAnimal, RotStage: domain.RotFresh},
		{Kind: "corpse", CorpseOf: domain.CorpseAnimal, RotStage: domain.RotRotting},
		{Kind: "corpse", CorpseOf: domain.CorpseStranger, RotStage: domain.RotRotting},
	})}
	needs := DumpNeeds(facts)
	if needs[domain.RottenDumpRole] != 1 || needs[domain.CorpseDumpRole] != 1 {
		t.Fatalf("needs %v", needs)
	}
}
