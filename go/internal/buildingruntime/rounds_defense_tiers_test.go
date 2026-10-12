package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Only a built killbox tier is pinned: not a pending one, a perimeter
// section or a removal.
func TestDefenseTierPinsAreTheBuiltKillboxTiers(t *testing.T) {
	wall := store.DefenseBuilding{Definition: "Wall", Cell: domain.Cell{X: 3, Z: 4}, Rotation: domain.North}
	record := store.DefenseLayoutRecord{
		TrapLane: []domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 2}},
		Firing:   []domain.Cell{{X: 5, Z: 5}},
		Retreat:  []domain.Cell{{X: 5, Z: 8}},
		Tiers: []store.DefenseTierRecord{
			{Name: policy.TierFunnel, Buildings: []store.DefenseBuilding{wall}, Reserved: []domain.Cell{{X: 3, Z: 4}}, Built: true},
			{Name: policy.TierTrapCorridor, Buildings: []store.DefenseBuilding{wall}},
			{Name: policy.TierPerimeterPrefix + "00", Buildings: []store.DefenseBuilding{wall}, Built: true},
			{Name: policy.TierFiringLine, Buildings: []store.DefenseBuilding{wall}, Built: true, Remove: true},
		},
	}
	pins := defenseTierPins(record)
	if len(pins.Tiers) != 1 {
		t.Fatalf("pins %v", pins.Tiers)
	}
	funnel := pins.Tiers[policy.TierFunnel]
	if len(funnel.Buildings) != 1 || funnel.Buildings[0].Cell() != wall.Cell || len(funnel.Reserved) != 1 {
		t.Fatalf("funnel %+v", funnel)
	}
	if len(pins.TrapLane) != 2 || len(pins.Firing) != 1 || pins.Firing[0].Retreat != (domain.Cell{X: 5, Z: 8}) {
		t.Fatalf("geometry %v %v", pins.TrapLane, pins.Firing)
	}
}
