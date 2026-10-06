package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

// A short clothing material opens its own source: the deer for leather, the
// field for cotton, each exactly once and the floor restored (#2169).
func TestClothingScenariosOpenTheirSource(t *testing.T) {
	t.Parallel()
	want := map[string]policy.AcquisitionKind{"Leather/leather short, deer available/none": policy.AcquisitionHunt, "Cotton/cotton field/none": policy.AcquisitionHarvest}
	for _, sc := range clothingScenarios() {
		run := runResScenario(sc)
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
