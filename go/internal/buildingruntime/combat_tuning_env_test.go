package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The test-only tuning override seeds every new fight's memory (both the
// ordinary admission and the sapper break); without it a fight carries none.
func TestNewFightMemoryCarriesTuningOverride(t *testing.T) {
	r := &RoundsDefensePlanner{}
	if m := r.newFightMemory(7); m.Tuning != nil || m.Tick != 7 {
		t.Fatalf("no override: memory %+v, want tick 7 and no tuning", m)
	}
	tuning := policy.DefaultCombatTuning()
	tuning.ScatterCells = 8
	m := r.WithCombatTuning(&tuning).newFightMemory(9)
	if m.Tuning == nil || *m.Tuning != tuning || m.Tick != 9 {
		t.Fatalf("override: memory %+v, want tick 9 and %+v", m, tuning)
	}
	tuning.ScatterCells = 45
	if m.Tuning.ScatterCells != 8 {
		t.Fatal("a fight's tuning aliases the planner's")
	}
}
