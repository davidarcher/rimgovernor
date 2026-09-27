package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestShellInteriorIsInsideThePerimeter pins the general store's footprint
// (#720): a 6x6 shell zones its 4x4 interior, never a wall or the door.
func TestShellInteriorIsInsideThePerimeter(t *testing.T) {
	var actions []domain.Action
	for x := int32(10); x < 16; x++ {
		for z := int32(20); z < 26; z++ {
			if x != 10 && x != 15 && z != 20 && z != 25 {
				continue
			}
			def := "Wall"
			if x == 12 && z == 20 {
				def = "Door"
			}
			b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: z}, domain.North, "WoodLog")
			if err != nil {
				t.Fatal(err)
			}
			a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("p-%d", len(actions))), b)
			if err != nil {
				t.Fatal(err)
			}
			actions = append(actions, a)
		}
	}
	spec, err := domain.NewPlan("p", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	cells := shellInterior(spec)
	if len(cells) != 16 || cells[0] != (domain.Cell{X: 11, Z: 21}) || cells[15] != (domain.Cell{X: 14, Z: 24}) {
		t.Fatal(cells)
	}
	if _, err := domain.NewStockpileZone(domain.GeneralPreset, domain.NormalPriority, cells); err != nil {
		t.Fatal(err)
	}
}
