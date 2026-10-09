package buildingruntime

import (
	"context"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoundsArmoryPlanner owns military production. The skeleton
// selects the armory tier from the storyteller's raid points and
// finished research and logs it. Weapon bills moved here from the gear
// planner and still bind to MaintainEquipment; the tier picks the
// weapon and gates upgrades, and the equip planner's crafting-spot
// fallback lives here too.
type RoundsArmoryPlanner struct {
	reviewer *Rounder
	native   RoundsGearSource
	// spot places a crafting spot when no bench hosts a weapon recipe for
	// an unarmed colonist; nil when the source cannot build.
	spot *RoundsBuildingPlanner
	mu   sync.Mutex
	// spotNeeded is the latest declaration's finding: an unarmed colonist and
	// no bench hosting a weapon recipe.
	spotNeeded bool
}

type RoundsArmoryResult struct {
	Verdict
	Plan domain.PlanID
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
	// The weapons, armor and shells are the ledger's (DeclareOrders); what is
	// left here is the crafting spot for colonists no bench can arm.
	_, workable, err := p.journal.Workable(call, review, policy.MaintainEquipment)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	if !workable || !r.spotWanted() {
		return RoundsArmoryResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	return r.placeCraftingSpot(call, epoch, arbiter)
}

// newCraftingSpotPlanner is the EnsureBasicDefense placement the armory
// falls back to (moved from the equip planner); nil when the source
// cannot serve a building step.
func newCraftingSpotPlanner(reviewer *Rounder, native RoundsGearSource) *RoundsBuildingPlanner {
	building, ok := native.(RoundsBuildingSource)
	if !ok {
		return nil
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: building, concern: policy.EnsureBasicDefense, definition: craftingSpotDefinition, environment: policy.PlacementAnywhere}
}
