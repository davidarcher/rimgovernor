package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// FoodStuckReason is why at-risk food sits unstored although a covered site
// has room. The zero value means no reason was named. No haul is planned for
// any of them: vanilla hauling moves loose food once the blocker clears.
type FoodStuckReason string

const (
	// FoodStuckForbidden: an unstored stack is forbidden, so no hauler touches it.
	FoodStuckForbidden FoodStuckReason = "stack_forbidden"
	// FoodStuckNoHauler: no colonist has Hauling enabled and prioritised.
	FoodStuckNoHauler FoodStuckReason = "no_colonist_can_haul"
	// FoodStuckUnestablished: neither cause can be shown from the facts (a
	// forbidden flag or the work census is unread, or both are ruled out). It
	// is reported as its own named reason, never folded into another.
	FoodStuckUnestablished FoodStuckReason = "cause_unestablished"
)

// FoodStorageStuckReason names why the unstored perishable stacks of the census
// are not stored. A known forbidden stack wins; otherwise a known empty hauler
// roster; anything else is FoodStuckUnestablished.
func FoodStorageStuckReason(observed FoodStorageObservation, p FoodStoragePolicy, haulers domain.Fact[[]PawnID]) FoodStuckReason {
	stocks, _ := observed.Stocks.Value()
	for _, entry := range stocks {
		if !FoodStorageUnstored(entry, observed.ChilledMaxC, p) {
			continue
		}
		if forbidden, known := entry.Stock.Forbidden.Value(); known && forbidden {
			return FoodStuckForbidden
		}
	}
	if rows, known := haulers.Value(); known && len(rows) == 0 {
		return FoodStuckNoHauler
	}
	return FoodStuckUnestablished
}
