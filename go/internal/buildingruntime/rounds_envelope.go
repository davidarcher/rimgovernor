package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// MaintainClimateEnvelope (#2962): the plan-wide owner of every planned room's
// vents and roof, and of the roofed hallway cells outside any room. A room's
// own concern builds them only while its mismatch check holds (reconcileRoom),
// so a built room whose owner has stopped reconciling would stand open to the
// sky and without its vent. This planner asks the plan instead: each top-level
// planned room is diffed ring-only and the vents and roofs the diff owes
// (policy.OpVentIn, policy.OpRoofIn) are committed through the same reconcile
// path as an owner's, whatever the owner is doing; nothing else of the room is
// touched. Roofs wait for the ring and vents for a standing wall, so a
// room not yet built owes nothing here.

// envelopeUnroofed is the mirror cells read open to the sky; empty while the
// roofed set is.
func envelopeUnroofed(facts observation.ColonyProjection, plan policy.LayoutPlan) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	if plan.Roofed.Empty() {
		return out
	}
	for _, c := range facts.Cells {
		if roofed, known := c.Roofed.Value(); known && !roofed {
			out[c.Cell] = true
		}
	}
	return out
}

// envelopeWork is the planned rooms owed a vent or a roof and the roofed
// hallway cells open to the sky; unknown while the plan or the construction
// census is.
func envelopeWork(facts observation.ColonyProjection) (rooms []policy.PlannedRoom, hallway []domain.Cell, known bool) {
	plan, planned := facts.LayoutPlan.Value()
	if !planned {
		return nil, nil, false
	}
	for _, room := range plan.AllRooms() {
		if room.Parent != 0 {
			continue // an airlock rides its parent's ring and roof
		}
		in, ground := roomRingInput(facts, plan, room)
		if !ground {
			return nil, nil, false
		}
		if len(policy.EnvelopeOps(policy.ReconcileRoom(in))) > 0 {
			rooms = append(rooms, room)
		}
	}
	if _, ground := colonyGround(facts); !ground {
		return nil, nil, false
	}
	return rooms, policy.EnvelopeHallwayRoof(plan, envelopeUnroofed(facts, plan)), true
}

// envelopeOwed is the review's EnvelopeOwed fact.
func envelopeOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	rooms, hallway, known := envelopeWork(facts)
	if !known {
		return domain.Unknown[bool]()
	}
	return domain.Known(len(rooms) > 0 || len(hallway) > 0)
}

// RoundsEnvelopePlanner stages the vents and roofs of MaintainClimateEnvelope.
type RoundsEnvelopePlanner struct {
	reviewer *Rounder
	// building commits the builds; nil for a source that cannot preview
	// buildings.
	building *RoundsBuildingPlanner
}

type RoundsEnvelopeResult struct {
	Verdict
}

func NewRoundsEnvelopePlanner(reviewer *Rounder) (*RoundsEnvelopePlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoundsEnvelopePlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	r := &RoundsEnvelopePlanner{reviewer: reviewer}
	if source, ok := reviewer.native.(RoundsBuildingSource); ok {
		r.building = &RoundsBuildingPlanner{reviewer: reviewer, native: source, concern: policy.MaintainClimateEnvelope}
	}
	return r, nil
}

func (r *RoundsEnvelopePlanner) step(call, epoch context.Context, _ *stepArbiter) (RoundsEnvelopeResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsEnvelopeResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoundsEnvelopeResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsEnvelopeResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsEnvelopeResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainClimateEnvelope)
	if err != nil {
		return RoundsEnvelopeResult{}, err
	}
	if !workable {
		return RoundsEnvelopeResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsEnvelopeResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsEnvelopeResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	if r.building == nil {
		return RoundsEnvelopeResult{Verdict: fieldUnavailable("building_source")}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsEnvelopeResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsEnvelopeResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim](), policy.ClimateDefs().VentDef)
	if err != nil {
		return RoundsEnvelopeResult{}, err
	}
	rooms, hallway, known := envelopeWork(reading.Projection)
	if !known {
		return RoundsEnvelopeResult{Verdict: fieldUnavailable("layout_plan")}, nil
	}
	verdict := BuildingReasonNoDeficit
	for _, room := range rooms {
		rr := roomReconcile{room: room, name: roomName("envelope", room), reason: "envelope", ringOnly: true, climateOnly: true}
		result, err := r.building.reconcileRoom(call, epoch, state, review, goal, reading, nil, rr)
		if err != nil {
			return RoundsEnvelopeResult{}, err
		}
		verdict = result.Verdict
		// A room with nothing ready (a roof already designated, a vent the
		// native refused) leaves the next its turn; work staged ends the step.
		if verdict.Outcome == OutcomeAdmitted {
			return RoundsEnvelopeResult{Verdict: verdict}, nil
		}
	}
	if len(hallway) > 0 {
		w := roomWork{rr: roomReconcile{name: "envelope-hallway", reason: "envelope"}, ops: []policy.Operation{{Kind: policy.OpRoofIn, Cells: hallway}}}
		result, done, err := r.building.commitRoof(call, epoch, state, goal, w)
		if err != nil {
			return RoundsEnvelopeResult{}, err
		}
		if done {
			return RoundsEnvelopeResult{Verdict: result.Verdict}, nil
		}
		if len(rooms) == 0 {
			verdict = BuildingReasonExistingWork
		}
	}
	return RoundsEnvelopeResult{Verdict: verdict}, nil
}
