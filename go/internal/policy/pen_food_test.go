package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestPenCellsBalancesTheGamesPenFoodSum(t *testing.T) {
	// 30 nutrition a day over cells growing 0.5 a day each is exactly 60 cells.
	if got := PenCells(30, 0.5); got != 60 {
		t.Fatalf("PenCells(30, 0.5) = %d, want 60", got)
	}
	// A fraction of a cell rounds up: the pen must cover the demand.
	if got := PenCells(30.1, 0.5); got != 61 {
		t.Fatalf("PenCells(30.1, 0.5) = %d, want 61", got)
	}
	if got := PenCells(1, 0.5); got != penCellsFloor {
		t.Fatalf("a tiny herd gets the floor %d, got %d", penCellsFloor, got)
	}
	if got := PenCells(30, 0); got != penCellsCap {
		t.Fatalf("a terrain that grows nothing gets the cap %d, got %d", penCellsCap, got)
	}
	if got := PenCells(1e9, 0.5); got != penCellsCap {
		t.Fatalf("a huge herd is capped at %d, got %d", penCellsCap, got)
	}
}

func TestPenDemandSumsAdultFeed(t *testing.T) {
	races := AnimalRaceCatalog{Races: map[Resource]AnimalRace{
		"Cow":   {AdultFeedPerDay: domain.Known(2.0)},
		"Sheep": {AdultFeedPerDay: domain.Known(0.5)},
	}}
	got, ok := PenDemand(races, map[Resource]int64{"Cow": 3, "Sheep": 4, "Horse": 0})
	if !ok || got != 8 {
		t.Fatalf("PenDemand = %v %v, want 8 true", got, ok)
	}
	if _, ok := PenDemand(races, map[Resource]int64{"Horse": 1}); ok {
		t.Fatal("a race the catalog lacks must read unknown")
	}
}

func TestPastureTableGrowsOnlyPositiveTerrains(t *testing.T) {
	table := PastureTable{"Soil": 0.4, "Sand": 0, "Gravel": 0.2}
	if v, ok := table.PerCell("Soil"); !ok || v != 0.4 {
		t.Fatalf("Soil = %v %v", v, ok)
	}
	if _, ok := table.PerCell("Sand"); ok {
		t.Fatal("a terrain at zero grows nothing")
	}
	if got := table.Terrains(); len(got) != 2 || got[0] != "Gravel" || got[1] != "Soil" {
		t.Fatalf("Terrains = %v", got)
	}
}
