package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoutineArmoryPlanner owns military production (#1198). The skeleton
// (#1201) selects the armory tier from the storyteller's raid points and
// finished research and logs it; it dispatches nothing yet.
type RoutineArmoryPlanner struct {
	reviewer *RoutineReviewer
}

type RoutineArmoryResult struct {
	Reason     RoutineBuildingReason
	Assessment policy.ArmoryAssessment
}

func NewRoutineArmoryPlanner(reviewer *RoutineReviewer) (*RoutineArmoryPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutineArmoryPlanner: reviewer == nil", ErrControl)
	}
	return &RoutineArmoryPlanner{reviewer}, nil
}

func (r *RoutineArmoryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineArmoryResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineArmoryResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineArmoryResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineArmoryResult{Reason: BuildingMethodNoReview}, nil
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	facts := read.Projection.Facts
	assessment := policy.AssessArmory(facts.RaidPoints, facts.Research)
	clockSchedulerLog("Armory.step tier=%s threat=%s research=%s", assessment.Tier, assessment.Threat, assessment.Research)
	return RoutineArmoryResult{Reason: BuildingMethodNoDeficit, Assessment: assessment}, nil
}
