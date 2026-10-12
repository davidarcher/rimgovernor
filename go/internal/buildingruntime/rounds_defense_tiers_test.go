package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A killbox tier is pinned once it is started: built, an attempt admitted, a
// lost building re-opening it, or any building standing; not a pending one
// nothing was done for.
func TestDefenseTierPinsAreTheStartedKillboxTiers(t *testing.T) {
	wall := store.DefenseBuilding{Definition: "Wall", Cell: domain.Cell{X: 3, Z: 4}, Rotation: domain.North}
	trap := store.DefenseBuilding{Definition: "TrapSpike", Cell: domain.Cell{X: 6, Z: 4}, Rotation: domain.North}
	record := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{
		{Name: policy.TierFunnel, Buildings: []store.DefenseBuilding{wall}},
		{Name: policy.TierTrapCorridor, Buildings: []store.DefenseBuilding{trap}},
		{Name: policy.TierTurrets, Buildings: []store.DefenseBuilding{wall}, Attempts: 1},
		{Name: policy.TierMortars, Buildings: []store.DefenseBuilding{wall}, Reopened: 1},
	}}
	if pins := defenseTierPins(record, nil); len(pins.Tiers) != 2 || pins.Tiers[policy.TierTurrets].Name == "" || pins.Tiers[policy.TierMortars].Name == "" {
		t.Fatalf("attempted and reopened tiers only: %v", pins.Tiers)
	}
	census := &defenseCensus{edifice: map[domain.Cell]string{wall.Cell: "Wall"}}
	pins := defenseTierPins(record, census)
	if _, ok := pins.Tiers[policy.TierFunnel]; !ok {
		t.Fatalf("a tier with a building standing is pinned: %v", pins.Tiers)
	}
	if _, ok := pins.Tiers[policy.TierTrapCorridor]; ok {
		t.Fatalf("a pending tier nothing stands for is pinned: %v", pins.Tiers)
	}
}

// Only a started killbox tier is pinned: not a perimeter section or a removal.
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
	pins := defenseTierPins(record, nil)
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
