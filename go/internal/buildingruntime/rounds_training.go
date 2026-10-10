package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// MaintainTraining (#2619): while a capable colonist is below the combat skill
// target the stockpile review asks layout for a training range
// (RoomDemand.Ranges, the stand count; a further graveyard is asked for the same
// way), and while two fighters are below the melee ceiling for a sparring ring
// (RoomDemand.Rings, the marker count; #2707). This planner then stages the
// planned range's stands and dummies and the ring's markers through the room
// reconciler. Both are unfenced open air (PlannedRoom.Unfenced), so the
// reconciler owes them no ring, roof or floor, only the template; one that
// outgrew its stands or markers is widened by layout and the same template builds
// the new ones. A standing range or ring takes no further action here: the
// native training and spar jobs spend the pawn-hours.

// trainingDefinitions are the definitions the range step reads availability
// and stuff for: the range's own buildings.
var trainingDefinitions = []string{policy.RangeDefNames[policy.RangeStand], policy.RangeDefNames[policy.RangeDummy], policy.RingMarkerDef}

// trainingDemand is the range's stands and the ring's markers the review wants,
// read from the projection's work pawns; unknown while they are.
func trainingDemand(facts observation.ColonyProjection) (stands, rings domain.Fact[int]) {
	pawns, known := facts.WorkPawns.Value()
	if !known {
		return domain.Unknown[int](), domain.Unknown[int]()
	}
	profiles := domain.Known(policy.Profiles(pawns))
	return policy.TrainingStands(profiles, facts.Facts.Research), policy.TrainingRings(profiles, facts.Facts.Research)
}

// RoundsTrainingPlanner stages the training range of MaintainTraining.
type RoundsTrainingPlanner struct {
	reviewer *Rounder
	// building stages the room; nil for a source that cannot preview buildings.
	building *RoundsBuildingPlanner
}

type RoundsTrainingResult struct {
	Verdict
}

func NewRoundsTrainingPlanner(reviewer *Rounder) (*RoundsTrainingPlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoundsTrainingPlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	r := &RoundsTrainingPlanner{reviewer: reviewer}
	if source, ok := reviewer.native.(RoundsBuildingSource); ok {
		r.building = &RoundsBuildingPlanner{reviewer: reviewer, native: source, concern: policy.MaintainTraining}
	}
	return r, nil
}

func (r *RoundsTrainingPlanner) step(call, epoch context.Context, _ *stepArbiter) (RoundsTrainingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsTrainingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoundsTrainingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsTrainingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsTrainingResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainTraining)
	if err != nil {
		return RoundsTrainingResult{}, err
	}
	if !workable {
		return RoundsTrainingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsTrainingResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsTrainingResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	if r.building == nil {
		return RoundsTrainingResult{Verdict: fieldUnavailable("building_source")}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsTrainingResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsTrainingResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim](), trainingDefinitions...)
	if err != nil {
		return RoundsTrainingResult{}, err
	}
	plan, known := reading.Projection.LayoutPlan.Value()
	if !known {
		return RoundsTrainingResult{Verdict: fieldUnavailable("layout_plan")}, nil
	}
	var rooms []roomReconcile
	if ranges := plan.RangeRooms(); len(ranges) > 0 {
		room := ranges[0]
		rooms = append(rooms, roomReconcile{room: room, template: policy.RangeTemplate(room), name: roomName("range", room), reason: "range"})
	}
	if rings := plan.RingRooms(); len(rings) > 0 {
		room := rings[0]
		rooms = append(rooms, roomReconcile{room: room, template: policy.RingTemplate(room), name: roomName("ring", room), reason: "ring"})
	}
	if len(rooms) == 0 {
		// The layout review grows the range and ring from RoomDemand.
		return RoundsTrainingResult{Verdict: waitFor(policy.CauseMethodUsed, "range_room")}, nil
	}
	stock := newPackedStock(r.reviewer.native, boundary.Identity(state.Snapshot))
	var verdict Verdict
	for _, rr := range rooms {
		result, err := r.building.reconcileRoom(call, epoch, state, review, goal, reading, stock, rr)
		if err != nil {
			return RoundsTrainingResult{}, err
		}
		verdict = result.Verdict
		// A room with nothing ready (finished, or waiting on its builders)
		// leaves the next one its turn; work staged ends the step.
		if !verdict.Is(policy.CauseExistingWork) {
			break
		}
	}
	return RoundsTrainingResult{Verdict: verdict}, nil
}
