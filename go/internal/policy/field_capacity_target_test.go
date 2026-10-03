package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shrink target must not sit below the cell count at which coverage
// reads 1, or shrink trims what growth keeps adding.
func TestFieldCapacityTargetAtLeastFieldTarget(t *testing.T) {
	crop := CropChoice{Demand: domain.Known(14.08), GrowDays: domain.Known(5.8), HarvestNutrition: domain.Known(0.6)}
	colonists := domain.Known(int64(8))
	grow, _ := FieldTarget(colonists, crop, 24, domain.Known(24.0)).Value()
	capacity, ok := FieldCapacityTarget(colonists, crop, 24).Value()
	if !ok || capacity < grow {
		t.Fatalf("capacity target %d (known %v) below field target %d", capacity, ok, grow)
	}
}
