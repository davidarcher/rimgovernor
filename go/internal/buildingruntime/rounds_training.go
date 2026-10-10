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
// way). This planner then stages the planned range's stands and dummies through
// the room reconciler. The range is unfenced open air (PlannedRoom.Unfenced), so
// the reconciler owes it no ring, roof or floor, only the template; a range that
// outgrew its stands is widened by layout and the same template builds the new
// ones. A standing range takes no further action here: the native training job
// (#2610) spends the pawn-hours.

// trainingDefinitions are the definitions the range step reads availability
// and stuff for: the range's own buildings.
var trainingDefinitions = []string{policy.RangeDefNames[policy.RangeStand], policy.RangeDefNames[policy.RangeDummy]}

// trainingStands is the stands the review wants, read from the projection's work
// pawns; unknown while they are.
func trainingStands(facts observation.ColonyProjection) domain.Fact[int] {
	pawns, known := facts.WorkPawns.Value()
	if !known {
		return domain.Unknown[int]()
	}
	return policy.TrainingStands(domain.Known(policy.Profiles(pawns)), facts.Facts.Research)
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
	ranges := plan.RangeRooms()
	if len(ranges) == 0 {
		// The layout review grows the range from RoomDemand.Ranges.
		return RoundsTrainingResult{Verdict: waitFor(WaitMethodUsed, "range_room")}, nil
	}
	stock := newPackedStock(r.reviewer.native, boundary.Identity(state.Snapshot))
	room := ranges[0]
	result, err := r.building.reconcileRoom(call, epoch, state, review, goal, reading, stock, roomReconcile{room: room, template: policy.RangeTemplate(room), name: roomName("range", room), reason: "range"})
	return RoundsTrainingResult{Verdict: result.Verdict}, err
}
