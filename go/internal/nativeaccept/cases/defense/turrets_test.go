package defense

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The planner routes the turret tier's chain in HiddenConduit (#405); the
// case must read those cells back as the tier's conduits (#703).
func TestTurretTierCellsReadsPlannerConduits(t *testing.T) {
	var buildings []domain.Building
	add := func(def string, x, z int32, stuff string) {
		b, err := domain.NewBuilding(def, domain.Cell{X: x, Z: z}, domain.North, stuff)
		if err != nil {
			t.Fatal(err)
		}
		buildings = append(buildings, b)
	}
	add(turretDefinition, 112, 124, "")
	for z := int32(118); z >= 116; z-- {
		add("HiddenConduit", 112, z, "")
	}
	add(turretDefinition, 109, 124, "")
	var record store.DefenseLayoutRecord
	record.SetTurretTier(policy.DefenseTier{Buildings: buildings})
	turrets, conduits, ok := turretTierCells(record)
	if !ok || len(turrets) != 2 || len(conduits) != 3 {
		t.Fatalf("turrets=%v conduits=%v ok=%v", turrets, conduits, ok)
	}
	if conduits[0] != (domain.Cell{X: 112, Z: 118}) {
		t.Fatalf("first conduit %v, want the cell nearest the turret", conduits[0])
	}
}
