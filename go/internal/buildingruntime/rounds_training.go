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
// (RoomDemand.Ranges, as a further graveyard is asked for). This planner then
// stages the planned range's shell and furniture through the room reconciler.
// A standing range takes no further action here: the native training job
// (#2610) spends the pawn-hours.

// trainingDefinitions are the definitions the range step reads availability
// and stuff for: the shell's wall and door and the range's own buildings.
var trainingDefinitions = func() []string {
	out := []string{policy.ShellWallDefinition, policy.ShellDoorDefinition}
	for _, kind := range []policy.RangePieceKind{policy.RangeStand, policy.RangeDummy, policy.RangePartition} {
		out = append(out, policy.RangeDefNames[kind])
	}
	return out
}()

// trainingGap is the review's standing skill gap, read from the projection's
// work pawns; unknown while they are.
func trainingGap(facts observation.ColonyProjection) domain.Fact[bool] {
	pawns, known := facts.WorkPawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	return policy.TrainingGap(domain.Known(policy.Profiles(pawns)))
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
