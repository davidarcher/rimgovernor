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

// Staging the tomb, the graveyard and the morgue (#832, #2196): MaintainBurial
// raises the planned tomb and places one sarcophagus at a time while a dead
// colonist has none waiting (policy.NextTombStep). With every tomb full the
// layout review grows another. Where no sarcophagus can be had one plain grave
// at a time is placed in the planned graveyard, whose fence and gate are raised
// with the first; with no slot left the body waits in the morgue and
// RoomDemand asks layout for a further graveyard. A waiting human corpse
// raises the morgue's shell. Vanilla haulers carry the corpses.

// roomName prefixes a room's reconcile methods: its role and interior corner.
func roomName(role string, room policy.PlannedRoom) string {
	return fmt.Sprintf("%s-%d-%d", role, room.Interior.X, room.Interior.Z)
}

// RoundsBurialPlanner stages the burial rooms of MaintainBurial.
type RoundsBurialPlanner struct {
	reviewer *Rounder
	// building stages the rooms; nil for a source that cannot preview
	// buildings.
	building *RoundsBuildingPlanner
}

type RoundsBurialResult struct {
	Verdict
}

func NewRoundsBurialPlanner(reviewer *Rounder) (*RoundsBurialPlanner, error) {
	if reviewer == nil || reviewer.native == nil {
		return nil, fmt.Errorf("%w: NewRoundsBurialPlanner: reviewer == nil || reviewer.native == nil", ErrControl)
	}
	r := &RoundsBurialPlanner{reviewer: reviewer}
	if source, ok := reviewer.native.(RoundsBuildingSource); ok {
		r.building = &RoundsBuildingPlanner{reviewer: reviewer, native: source, concern: policy.MaintainBurial}
	}
	return r, nil
}

func (r *RoundsBurialPlanner) step(call, epoch context.Context, _ *stepArbiter) (RoundsBurialResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBurialResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoundsBurialResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBurialResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsBurialResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainBurial)
	if err != nil {
		return RoundsBurialResult{}, err
	}
	if !workable {
		return RoundsBurialResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsBurialResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsBurialResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	if r.building == nil {
		return RoundsBurialResult{Verdict: fieldUnavailable("building_source")}, nil
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsBurialResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsBurialResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim](), burialDefinitions...)
	if err != nil {
		return RoundsBurialResult{}, err
	}
	if err = r.reviewer.reviewStrangers(call, state.Snapshot, &reading.Projection); err != nil {
		return RoundsBurialResult{}, err
	}
	step := tombStep(reading.Projection)
	stock := newPackedStock(r.reviewer.native, boundary.Identity(state.Snapshot))
	var result RoundsBuildingResult
	switch step.Kind {
	case policy.TombDispose:
		return r.dispose(call, epoch, state, review, goal, reading, step.Disposal[0])
	case policy.TombNone:
		morgue, owed := plannedMorgue(reading.Projection)
		if !owed {
			return RoundsBurialResult{Verdict: BuildingReasonNoDeficit}, nil
		}
		result, err = r.building.reconcileRoom(call, epoch, state, review, goal, reading, stock, roomReconcile{room: morgue, name: roomName("morgue", morgue), reason: "morgue"})
	case policy.TombReconcile:
		role := "tomb"
		if step.Room.Role == policy.PlannedGraveyard {
			role = "graveyard"
		}
		result, err = r.building.reconcileRoom(call, epoch, state, review, goal, reading, stock, roomReconcile{room: step.Room, template: step.Template, name: roomName(role, step.Room), reason: role})
	case policy.TombFull:
		// The layout review grows another tomb.
		return RoundsBurialResult{Verdict: waitFor(WaitMethodUsed, "tomb_room")}, nil
	}
	return RoundsBurialResult{Verdict: result.Verdict}, err
}

// dispose deconstructs one filled stranger sarcophagus (#2337) with the plain
// Deconstruction action, once per sarcophagus per Episode: native ejects the
// corpse beside the cell for the incineration concern to burn.
func (r *RoundsBurialPlanner) dispose(call, epoch context.Context, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, b policy.CurrentBuilding) (RoundsBurialResult, error) {
	result, err := r.building.retireBuilding(call, epoch, state, review, goal, reading.ColonyReading, b.ID, b.Building.Definition(), b.Building.Cell(), "tomb-dispose", "tomb_disposal")
	return RoundsBurialResult{Verdict: result.Verdict}, err
}
