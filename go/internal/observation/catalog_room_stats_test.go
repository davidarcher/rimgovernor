package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The impressiveness thresholds and the roof support radius come from the
// recorded game's rows and constants.
func TestRecordedCatalogImpressivenessAndRoofSupport(t *testing.T) {
	catalog := recordedCatalog(t)
	levels, err := catalog.ImpressivenessLevels()
	if err != nil {
		t.Fatal(err)
	}
	if len(levels.Stages) <= 4 || levels.Stage(40) != 3 {
		t.Errorf("stages %v", levels.Stages)
	}
	levels.Stages = nil
	if want := (policy.ImpressivenessLevels{Dull: 20, Mediocre: 30, Decent: 40, SlightlyImpressive: 50}); levels.Dull != want.Dull || levels.Mediocre != want.Mediocre || levels.Decent != want.Decent || levels.SlightlyImpressive != want.SlightlyImpressive {
		t.Errorf("levels %+v, want %+v", levels, want)
	}
	if got := catalog.Constants.RoofMaxSupportDistance; got != 6.9 {
		t.Errorf("roof support %v, want 6.9", got)
	}
}
