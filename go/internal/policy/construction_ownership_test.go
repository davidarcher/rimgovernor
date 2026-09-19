package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestConstructionOwnershipRequiresIdentityAndUnchangedGeometry(t *testing.T) {
	building, err := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 7}, domain.East, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	claim := ConstructionClaim{Plan: "method", Action: "action", Goal: "goal", Identity: domain.ConstructionIdentity{Origin: "blueprint", Current: "wall"}, Building: building}
	for _, kind := range []string{"same", "replacement", "moved", "rotated", "material", "missing", "unknown"} {
		current := CurrentBuilding{ID: "wall", Building: building}
		cell, rotation, stuff := building.Cell(), building.Rotation(), building.Stuff()
		switch kind {
		case "replacement":
			current.ID = "new-wall"
		case "moved":
			cell.X++
		case "rotated":
			rotation = domain.North
		case "material":
			stuff = "GraniteBlocks"
		}
		current.Building, _ = domain.NewBuilding("Wall", cell, rotation, stuff)
		observed := domain.Known(CurrentConstruction{Requested: []string{"wall", "new-wall"}, Buildings: []CurrentBuilding{current}})
		if kind == "missing" {
			observed = domain.Known(CurrentConstruction{Requested: []string{"wall"}})
		}
		if kind == "unknown" {
			observed = domain.Unknown[CurrentConstruction]()
		}
		got, err := OwnedConstructions(domain.Known([]ConstructionClaim{claim}), observed)
		rows, known := got.Value()
		if err != nil || known != (kind != "unknown") || (len(rows) == 1) != (kind == "same") {
			t.Fatal(kind, rows, known, err)
		}
	}
}

func TestObservedPlayerFacilitiesNeedNoAutonomousHistory(t *testing.T) {
	wall := facilityClaim(t, "player-wall", "Wall", "WoodLog")
	bed := facilityClaim(t, "player-bed", "Bed", "WoodLog")
	census := CurrentConstruction{Colony: true, Buildings: []CurrentBuilding{
		{ID: wall.Identity.Current, Building: wall.Building, Cells: []domain.Cell{wall.Building.Cell()}},
		{ID: bed.Identity.Current, Building: bed.Building, Cells: []domain.Cell{bed.Building.Cell()}},
	}}
	for _, claims := range []domain.Fact[[]ConstructionClaim]{domain.Unknown[[]ConstructionClaim](), domain.Known([]ConstructionClaim{}), domain.Known([]ConstructionClaim{wall})} {
		owned, err := OwnedConstructions(claims, domain.Known(census))
		rows, known := owned.Value()
		if err != nil || !known || len(rows) != 2 || rows[0].Identity.Current != "player-bed" || rows[0].Action != "" {
			t.Fatal(rows, known, err)
		}
		home := HomeCoverageObservation{Targets: []HomeCoverageTarget{{ID: "player-bed", Shape: domain.Known("shape"), Missing: domain.Known(int64(1)), Excluded: domain.Known(int64(0)), Cells: []domain.Cell{bed.Building.Cell()}}}}
		targets, err := ReviewHomeCoverage(owned, domain.Known([]OwnedStockpile{}), domain.Known(home))
		h, hk := targets.Value()
		if err != nil || !hk || len(h) != 1 {
			t.Fatal(h, hk, err)
		}
		stone, err := ReviewStoneShell(owned, domain.Known([]StoneStructure{{ID: "player-wall", Definition: "Wall", Flammability: domain.Known(1.0)}}))
		walls, wk := stone.Value()
		if err != nil || !wk || len(walls) != 1 || walls[0] != "player-wall" {
			t.Fatal(walls, wk, err)
		}
	}
	// A replacement is eligible on current facts without inheriting old lineage.
	moved, _ := domain.NewBuilding("Wall", domain.Cell{X: 4, Z: 7}, domain.North, "WoodLog")
	census.Buildings = []CurrentBuilding{{ID: wall.Identity.Current, Building: moved, Cells: []domain.Cell{moved.Cell()}}}
	owned, err := OwnedConstructions(domain.Known([]ConstructionClaim{wall}), domain.Known(census))
	rows, known := owned.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].Action != "" || rows[0].Building != moved {
		t.Fatal(rows, known, err)
	}
	census.Buildings[0].Cells = nil
	if _, err := OwnedConstructions(domain.Known([]ConstructionClaim{}), domain.Known(census)); err == nil {
		t.Fatal("missing footprint accepted")
	}
}
