package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// incinerationDefinitions are the definitions the incinerator and yard shells
// read availability for.
var incinerationDefinitions = []string{"Wall", "Door"}

// RoundsIncinerationPlanner composes MaintainIncineration's methods (the
// Sanitation department's incinerator): the waste yard's and the incinerator's
// shell, the burn of a full incinerator and the ash cleanup. It reads what
// the waste census reads: the colony and the tend pawn rows for burner
// and cleaner eligibility.
type RoundsIncinerationPlanner struct {
	reviewer *Rounder
	native   RoundsWasteSource
	// building shells the rooms; nil for a source that cannot preview
	// buildings.
	building *RoundsBuildingPlanner
}

type RoundsIncinerationResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsIncinerationPlanner(reviewer *Rounder, native RoundsWasteSource) (*RoundsIncinerationPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsIncinerationPlanner: reviewer == nil || native == nil", ErrControl)
	}
	r := &RoundsIncinerationPlanner{reviewer: reviewer, native: native}
	if source, ok := native.(RoundsBuildingSource); ok {
		r.building = &RoundsBuildingPlanner{reviewer: reviewer, native: source, concern: policy.MaintainIncineration}
	}
	return r, nil
}

func (r *RoundsIncinerationPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsIncinerationResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsIncinerationResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoundsIncinerationResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsIncinerationResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsIncinerationResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainIncineration)
	if err != nil {
		return RoundsIncinerationResult{}, err
	}
	if !workable {
		return RoundsIncinerationResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// MaintainIncineration competes for the same bounded concurrent-project
	// capacity as the other priority>=3 goals; only act while this review's
	// arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Concern == policy.MaintainIncineration && row.Selected
	}
	if !selected {
		return RoundsIncinerationResult{Verdict: awaitingSlot(string(policy.MaintainIncineration))}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsIncinerationResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsIncinerationResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	source, ok := r.native.(observation.RoundsSource)
	if !ok || r.building == nil {
		return RoundsIncinerationResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoundsIncinerationResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoundsIncinerationResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsIncinerationResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeRooms(call, source, expected, domain.Unknown[[]policy.ConstructionClaim](), incinerationDefinitions...)
	if err != nil {
		return RoundsIncinerationResult{}, err
	}
	result, handled, err := r.stageDisposal(call, epoch, state, review, goal, arbiter, reading)
	if err != nil || handled {
		return result, err
	}
	return RoundsIncinerationResult{Verdict: waitFor(WaitMethodUsed, "incineration")}, nil
}

// RoundsWasteSource reuses the generic colony read for the waste census
// and the existing tend pawn read for burner and cleaner eligibility: dead, downed, drafted and mental
// state only . No new native call is introduced for this slice.
type RoundsWasteSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

// colonistRows are the detail rows of every colonist, in one tend-pawn read;
// ok is false while the colonist list is incomplete or empty.
func colonistRows(call context.Context, native RoundsWasteSource, state ControlState, review store.Rounds) ([]*n.PawnState, bool, error) {
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := native.ReadEmergency(call, identityRef)
	if err != nil {
		return nil, false, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return nil, false, fmt.Errorf("%w: colonistRows: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return nil, false, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return nil, false, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return nil, false, fmt.Errorf("%w: colonistRows: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return nil, false, fmt.Errorf("%w: colonistRows: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return nil, false, fmt.Errorf("%w: colonistRows: len(observed.Pawns) != len(ids)", ErrControl)
	}
	return observed.Pawns, true, nil
}
