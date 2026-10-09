package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	partySpotRetirePrefix = "party-spot-retire"
	partySpotPlacePrefix  = "party-spot-place"
)

// retireMethodID is the method retireBuilding binds the deconstruction of one
// building under: once per building per Episode.
func retireMethodID(prefix, id string) domain.MethodID {
	sum := sha256.Sum256([]byte(id))
	return domain.MethodID(fmt.Sprintf("%s-%x", prefix, sum[:8]))
}

// partySpotPlaceMethod is the placement method for one shared room (room ""
// is the any-cell placement): once per room per Episode.
func partySpotPlaceMethod(room string) domain.MethodID {
	return retireMethodID(partySpotPlacePrefix, "room:"+room)
}

// partySpotSpent reports which one-shots the Episode's methods already used.
func partySpotSpent(methods []domain.Method) func(retire bool, id string) bool {
	used := map[domain.MethodID]bool{}
	for _, m := range methods {
		used[m.Method] = true
	}
	return func(retire bool, id string) bool {
		if retire {
			return used[retireMethodID(partySpotRetirePrefix, id)]
		}
		return used[partySpotPlaceMethod(id)]
	}
}

// partySpotReview reads the PartySpot need from one projection. spent names
// the one-shots the open Episode used up.
func partySpotReview(facts observation.ColonyProjection, spent func(bool, string) bool) domain.Fact[policy.PartySpotNeed] {
	sleeping, _ := facts.Facts.Sleeping.Value()
	return policy.ReviewPartySpot(policy.PartySpotInput{
		Rooms:     facts.Rooms,
		Qualities: sleeping.Rooms,
		Levels:    facts.Impressiveness,
		Census:    facts.Facts.CurrentConstruction,
		Cells:     facts.Cells,
		Placeable: comfortBuilderAvailable(facts, policy.PartySpotDefinition),
		Spent:     spent,
	})
}

// partySpotOwed is the review's PartySpotOwed fact. The Episode's spent
// one-shots are those of the standing EnsureComfort goal while it is Unmet; a
// goal that is Met opens a fresh Episode when it is raised again, so nothing
// is spent yet.
func (r *Rounder) partySpotOwed(ctx context.Context, previous store.Rounds, facts observation.ColonyProjection) (domain.Fact[bool], error) {
	var spent func(bool, string) bool
	for _, binding := range previous.Standards {
		if binding.Concern != policy.EnsureComfort {
			continue
		}
		state, err := r.player.journal.LoadStandard(ctx, binding.Standard)
		if err != nil {
			return domain.Unknown[bool](), err
		}
		if state.Standard.Finding == domain.FindingUnmet {
			spent = partySpotSpent(state.OwnerMethods())
		}
	}
	need, known := partySpotReview(facts, spent).Value()
	if !known {
		return domain.Unknown[bool](), nil
	}
	return domain.Known(need.Owed()), nil
}

// partySpotStep resolves the spot step for the ranked comfort planner: a
// deconstruction (done here) or a placement the shared placement path takes
// over, with the winning room's cells.
func (r *RoundsBuildingPlanner) partySpotStep(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.ColonyReading) (RoundsBuildingResult, *RoundsBuildingPlanner, error) {
	need, known := partySpotReview(reading.Projection, partySpotSpent(goal.OwnerMethods())).Value()
	if !known || !need.Owed() {
		return RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}, nil, nil
	}
	if need.HasRetire {
		b := need.Retire
		result, err := r.retireBuilding(call, epoch, state, review, goal, reading, b.ID, b.Building.Definition(), b.Cells[0], partySpotRetirePrefix, "party_spot_retire_method")
		return result, nil, err
	}
	placing := *r
	placing.cells = need.Cells
	placing.partySpotRoom = need.Room
	return RoundsBuildingResult{}, &placing, nil
}
