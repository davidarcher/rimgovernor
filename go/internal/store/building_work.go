package store

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// PlanWorkOpen is policy.PlanWorkOpen for a stored plan (#856): an applied
// building intent is terminal in the journal, but its plan stays open while
// the census still shows its blueprint or frame (or cannot say). Only
// census-aware retirement ends it; a retired plan's applied buildings are
// done or gone.
func PlanWorkOpen(p PlanState, census domain.Fact[policy.CurrentConstruction]) bool {
	if p.Retired {
		return domain.StandardWorkOpen(p.Progress)
	}
	return policy.PlanWorkOpen(p.Progress, census)
}

// PlanOpen is a plan's open work without a census: an applied building on
// a plan not yet retired is still open, since retirement is what reads the
// census (retireRoundsPlans).
func PlanOpen(p PlanState) bool {
	return PlanWorkOpen(p, domain.Unknown[policy.CurrentConstruction]())
}

// ProgressOpen is PlanOpen for one action of plan p.
func ProgressOpen(p PlanState, progress domain.Progress) bool {
	return domain.StandardWorkOpen([]domain.Progress{progress}) || !p.Retired && policy.AppliedBuildingOpen(progress, domain.Unknown[policy.CurrentConstruction]())
}
