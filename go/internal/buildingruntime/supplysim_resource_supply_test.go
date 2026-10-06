package buildingruntime

import (
	"slices"
	"sort"
	"testing"
)

// The resource matrix through policy.PlanSupply (epic #2140, #2155): the same
// scenarios and checks as TestResourceMatrixShrinksBaseline, with the four
// planners and the bid board replaced by one PlanSupply call over every unmet
// floor. The failing set must stay inside the PlanFood-era baseline; a
// baselined check PlanSupply fixes is logged as a flip.
func TestResourceMatrixPlanSupply(t *testing.T) {
	t.Parallel()
	base := readResourceBaseline(t)
	current := map[string]string{}
	for _, sc := range resScenarios() {
		for k, v := range runResScenarioWith(sc, true).failures() {
			current[k] = v
		}
	}
	var keys []string
	for k := range current {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		if _, known := base.Failing[k]; !known {
			t.Errorf("PlanSupply fails %s, which is not in the baseline: %s", k, current[k])
		}
	}
	keys = keys[:0]
	for k := range base.Failing {
		if _, still := current[k]; !still {
			keys = append(keys, k)
		}
	}
	slices.Sort(keys)
	for _, k := range keys {
		t.Logf("flip: %s now passes under PlanSupply", k)
	}
}
