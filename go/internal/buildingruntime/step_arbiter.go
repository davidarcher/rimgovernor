package buildingruntime

import (
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// stepArbiter prevents two planners running concurrently within one
// ClockScheduler.Step() call from both claiming the same pawn or the same
// non-pawn resource (a bench, an item slot, ...). It is scoped to exactly one
// Step() call: fresh at the top, discarded at the end, never shared across
// ticks.
type stepArbiter struct {
	mu        sync.Mutex
	pawns     map[domain.PawnID]bool
	resources map[string]bool // namespaced, e.g. "haul-item:<id>", "bench:<id>"
}

func newStepArbiter() *stepArbiter {
	return &stepArbiter{pawns: map[domain.PawnID]bool{}, resources: map[string]bool{}}
}

// tryClaim reserves every given pawn and resource atomically, or reserves
// none and returns false if any is already claimed by another planner this
// Step().
func (a *stepArbiter) tryClaim(pawns []domain.PawnID, resources ...string) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	for _, p := range pawns {
		if a.pawns[p] {
			return false
		}
	}
	for _, r := range resources {
		if a.resources[r] {
			return false
		}
	}
	for _, p := range pawns {
		a.pawns[p] = true
	}
	for _, r := range resources {
		a.resources[r] = true
	}
	return true
}
