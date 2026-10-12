package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestHaulModelFromRecordedCatalog reads the mined and chopped goods of the
// recorded game (#2998): a steel tile is 1500 hit points of 80-damage hits at
// 100 ticks (19 hits) for 40 steel; the trees of the wood item average.
func TestHaulModelFromRecordedCatalog(t *testing.T) {
	m := fullCatalog(t).haulModel()
	byGood := func(list []policy.HaulSource, good policy.Resource) (policy.HaulSource, bool) {
		for _, s := range list {
			if s.Good == good {
				return s, true
			}
		}
		return policy.HaulSource{}, false
	}
	steel, ok := byGood(m.Mined, "Steel")
	if !ok || steel.Units != 40 || steel.Work != 1900 {
		t.Errorf("steel = %+v, found %v", steel, ok)
	}
	if _, built := byGood(m.Mined, "Granite"); built {
		t.Error("a rock's own name is a yield")
	}
	for _, s := range append(append([]policy.HaulSource{}, m.Mined...), m.Chopped...) {
		if !(s.Units > 0) || !(s.Work > 0) || math.IsInf(s.Units, 0) || math.IsInf(s.Work, 0) {
			t.Errorf("source %+v holds an unread number", s)
		}
	}
	wood, ok := byGood(m.Chopped, "WoodLog")
	if !ok || wood.Units < 5 || wood.Units > 60 || wood.Work < 200 || wood.Work > 1800 {
		t.Errorf("wood = %+v, found %v", wood, ok)
	}
	t.Logf("mined %+v chopped %+v", m.Mined, m.Chopped)
}
