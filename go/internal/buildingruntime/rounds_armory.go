package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoundsArmoryPlanner owns military production (#1198). The skeleton
// (#1201) selects the armory tier from the storyteller's raid points and
// finished research and logs it. Weapon bills moved here from the gear
// planner (#1203) and still bind to MaintainEquipment; the tier picks the
// weapon and gates upgrades (#1204), and the equip planner's crafting-spot
// fallback lives here too.
type RoundsArmoryPlanner struct {
	reviewer *Rounder
	native   RoundsGearSource
	// spot places a crafting spot when no bench hosts a weapon recipe for
	// an unarmed colonist; nil when the source cannot build.
	spot *RoundsBuildingPlanner
}

type RoundsArmoryResult struct {
	Verdict
	Assessment policy.ArmoryAssessment
	Plan       domain.PlanID
}

func NewRoundsArmoryPlanner(reviewer *Rounder, native RoundsGearSource) (*RoundsArmoryPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsArmoryPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsArmoryPlanner{reviewer: reviewer, native: native, spot: newCraftingSpotPlanner(reviewer, native)}, nil
}

func (r *RoundsArmoryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsArmoryResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsArmoryResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoundsArmoryResult{}, fmt.Errorf("%w: step: !state.ObservationKnown", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsArmoryResult{Verdict: BuildingReasonNoReview}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	facts := read.Projection.Facts
	assessment := policy.AssessArmory(facts.RaidPoints, facts.Research)
	result, err := r.craftWeapons(call, epoch, arbiter, state, review, assessment.Tier, facts.Items.StuffCategories)
	if err == nil && result.Verdict != BuildingReasonAdmitted {
		result, err = r.stockShells(call, epoch, state, review, read.Projection)
	}
	result.Assessment = assessment
	return result, err
}

// newCraftingSpotPlanner is the EnsureBasicDefense placement the armory
// falls back to (moved from the equip planner, #1204); nil when the source
// cannot serve a building step.
func newCraftingSpotPlanner(reviewer *Rounder, native RoundsGearSource) *RoundsBuildingPlanner {
	building, ok := native.(RoundsBuildingSource)
	if !ok {
		return nil
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: building, concern: policy.EnsureBasicDefense, definition: craftingSpotDefinition, environment: policy.PlacementAnywhere}
}
