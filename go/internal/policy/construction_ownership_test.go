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
