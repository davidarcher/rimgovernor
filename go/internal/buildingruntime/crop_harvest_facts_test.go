package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A crop choice carries the definition's harvest facts and keeps unknown
// ones unknown (#2282).
func TestWithHarvestFacts(t *testing.T) {
	d := observation.PlanningDefinition{HarvestedThingDef: domain.Known("Cloth"), HarvestYield: domain.Known(7.0), SowMinSkill: domain.Known(int32(6)), HarvestDestroysPlant: domain.Known(true)}
	got := withHarvestFacts(policy.CropChoice{Name: "Plant_Cotton"}, d)
	if got.Harvests != domain.Known(policy.Resource("Cloth")) || got.UnitsPerCell != domain.Known(7.0) || got.SowMinSkill != domain.Known(int32(6)) || got.HarvestDestroys != domain.Known(true) {
		t.Fatalf("known: %+v", got)
	}
	none := withHarvestFacts(policy.CropChoice{Name: "Plant_Bare"}, observation.PlanningDefinition{})
	if none.Harvests != domain.Unknown[policy.Resource]() || none.UnitsPerCell != domain.Unknown[float64]() || none.HarvestDestroys != domain.Unknown[bool]() {
		t.Fatalf("unknown: %+v", none)
	}
}
