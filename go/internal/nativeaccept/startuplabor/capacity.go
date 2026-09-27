package startuplabor

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ContinuationBudget is how long a review may leave a distinct unused
// worker beside runnable ready work without admitting anything or naming
// an enforced constraint (#655): three labor-idle release periods (2,500
// ticks each), enough for a review, a planner step and a yield to land.
const ContinuationBudget domain.Tick = 7500

// CapacitySample is one automatic-mode review as the development record
// and the ready-work projection filed it.
type CapacitySample struct {
	Tick domain.Tick
	// Unused is the census's unmatched distinct workers; unknown when the
	// review had no census.
	Unused domain.Fact[int]
	// Runnable is the ready-work projection's runnable candidate count.
	Runnable int
	// Committed is the number of committed development goals.
	Committed int
	// Limiting is the reason the review gave for admitting no more.
	Limiting policy.DevelopmentReason
}

// Stranding is a stretch over which unused workers and runnable work
// coexisted with no admission and no enforced constraint.
type Stranding struct {
	From, To domain.Tick
	Unused   int
	Runnable int
	Limiting policy.DevelopmentReason
}
