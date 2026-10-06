package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"slices"
	"sort"
	"testing"
)

// The resource matrix through policy.PlanSupply (epic #2140, #2155): the same
// scenarios and checks as TestResourceMatrixShrinksBaseline, with the four
// planners and the bid board replaced by one PlanSupply call over every unmet
// floor. The failing set must stay inside the resource baseline; a
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

// A short clothing material opens its own source: the deer for leather, the
// field for cotton, each exactly once and the floor restored (#2169).
func TestClothingScenariosOpenTheirSource(t *testing.T) {
	t.Parallel()
	want := map[string]policy.AcquisitionKind{"Leather/leather short, deer available/none": policy.AcquisitionHunt, "Cotton/cotton field/none": policy.AcquisitionHarvest}
	for _, sc := range clothingScenarios() {
		run := runResScenarioWith(sc, true)
		if f := run.failures(); len(f) != 0 {
			t.Errorf("%s: %v", sc.name, f)
		}
		var kinds []policy.AcquisitionKind
		for _, e := range run.pl.events {
			kinds = append(kinds, e.Kind)
		}
		if len(kinds) == 0 || kinds[0] != want[sc.name] {
			t.Errorf("%s opened %v, want %s first", sc.name, kinds, want[sc.name])
		}
	}
}
