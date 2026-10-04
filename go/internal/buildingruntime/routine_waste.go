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

// RoutineWasteSource reuses the generic colony read for the waste census
// and the existing tend pawn read for hauler eligibility: dead, downed, drafted and mental
// state only (waste, unlike cleaning, gates on none of Cleaning's work
// setting or health facts). No new native call is introduced for this slice.
type RoutineWasteSource interface {
	observation.ColonySource
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

type RoutineWastePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineWasteSource
	// building stages the tomb (#832); nil for a source that cannot
	// preview buildings.
	building *RoutineBuildingPlanner
}
type RoutineWasteResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineWastePlanner(reviewer *RoutineReviewer, native RoutineWasteSource) (*RoutineWastePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineWastePlanner: reviewer == nil || native == nil", ErrControl)
	}
	r := &RoutineWastePlanner{reviewer: reviewer, native: native}
	if source, ok := native.(RoutineBuildingSource); ok {
		r.building = &RoutineBuildingPlanner{reviewer: reviewer, native: source, goal: policy.MaintainWaste}
	}
	return r, nil
}
func (r *RoutineWastePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineWasteResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineWasteResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineWasteResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineWasteResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainWaste)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if !workable {
		return RoutineWasteResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// MaintainWaste competes for the same bounded concurrent-project capacity
	// as comfort/expansion/other priority>=3 autopilot goals; only act while
	// this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainWaste && row.Selected
	}
	if !selected {
		return RoutineWasteResult{Verdict: awaitingSlot(string(policy.MaintainWaste))}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineWasteResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineWasteResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineWasteResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	if result, handled, err := r.stageTomb(call, epoch, state, review, goal, arbiter, expected); err != nil || handled {
		return result, err
	}
	started := r.reviewer.clock.Now()
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	items, known := reading.Projection.Facts.Waste.Value()
	if !known || len(items) == 0 {
		return RoutineWasteResult{Verdict: waitFor(WaitMethodUsed, "waste_items")}, nil
	}
	rows, ok, err := r.colonistRows(call, state, review)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if !ok {
		return RoutineWasteResult{Verdict: waitFor(WaitMethodUsed, "colonist_rows")}, nil
	}
	var pawns []policy.WastePawn
	seen := map[string]bool{}
	for _, row := range rows {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineWasteResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		pawns = append(pawns, wasteCandidateFacts(pawn, row))
	}
	item, pawn, ok := policy.SelectWasteMethod(items, pawns)
	if !ok {
		return RoutineWasteResult{Verdict: waitFor(WaitMethodUsed, "waste_method")}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(pawn)}, "waste-item:"+item.ID) {
		return RoutineWasteResult{Verdict: claimHeld("colonist")}, nil
	}
	waste, err := domain.NewWaste(domain.PawnID(pawn), item.ID, item.Cell)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	// Keyed by item and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever hauler is currently best.
	prefix := fmt.Sprintf("waste-%s-", item.ID)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineWasteResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	action, err := domain.NewWasteAction(domain.ActionID(fmt.Sprintf("%s-0", id)), waste)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineWasteResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineWasteResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineWasteResult{}, err
	}
	return RoutineWasteResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

func wasteCandidateFacts(pawn domain.PawnID, row *n.PawnState) policy.WastePawn {
	facts := policy.WastePawn{ID: policy.PawnID(pawn), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	return facts
}

// colonistRows are the detail rows of every colonist, in one tend-pawn read;
// ok is false while the colonist list is incomplete or empty.
func (r *RoutineWastePlanner) colonistRows(call context.Context, state ControlState, review store.RoutineReview) ([]*n.PawnState, bool, error) {
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
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
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
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
