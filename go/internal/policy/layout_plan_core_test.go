package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestPlanCoreOffCentre: on a survey whose only ground is the
// south-west corner, the plan core (the anchor for stockpiles and the
// cooking campfire, #1534) lies in that corner, not at the map centre.
func TestPlanCoreOffCentre(t *testing.T) {
	zones := Zone(zoningSurvey(120, func(x, z int32) SurveyCell {
		if x < 50 && z < 50 {
			return SurveyCell{Walkable: true, Fertility: 1}
		}
		return SurveyCell{}
	}))
	plan := corePlan(zones, 4, BuildTierCamp)
	core, ok := plan.Core()
	if !ok {
		t.Fatalf("no core in plan %s", plan.Summary())
	}
	corner, centre := domain.Cell{X: 25, Z: 25}, domain.Cell{X: 60, Z: 60}
	if squaredDistance(core, corner) >= squaredDistance(core, centre) {
		t.Fatalf("core %v nearer the map centre than the buildable corner; plan %s", core, plan.Summary())
	}
	if _, ok := (LayoutPlan{}).Core(); ok {
		t.Fatal("empty plan reported a core")
	}
}
