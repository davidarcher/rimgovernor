package combatlab

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// TestSplitRangedFirstDecisionHoldsTheLine pins the real service's first
// orders on lab-split-ranged (#2994): the four riflemen already stand on the
// layout's firing cells, whose cover scores tie exactly in the native reply,
// so every one is told to hold. Double-precision cover values tied by rounding
// noise sent each colonist to a neighbour's cell, 28 ticks of walking before
// the first shot.
func TestSplitRangedFirstDecisionHoldsTheLine(t *testing.T) {
	cx, cz := labCenter()
	fight, err := importFight(recordedcatalog.Catalog(t), "lab-split-ranged", cx, cz)
	if err != nil {
		t.Fatal(err)
	}
	var sim combatsim.Sim
	if _, err := sim.Reset(fight.Scenario, 1); err != nil {
		t.Fatal(err)
	}
	a, err := combatsim.NewAdapter(fight, &sim)
	if err != nil {
		t.Fatal(err)
	}
	orders, _ := a.Decide(policy.StopEvent{}, policy.CombatMemory{})
	if len(orders) != 4 {
		t.Fatalf("%d orders, want 4: %+v", len(orders), orders)
	}
	for _, o := range orders {
		if o.Kind != policy.OrderHoldPosition {
			t.Errorf("order %+v, want hold_position", o)
		}
	}
}
