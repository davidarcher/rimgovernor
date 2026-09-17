package buildingruntime

import (
	"context"
	"crypto/sha256"
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
// (already carries each item's cell, unlike the exact-ID CAS lookup this
// census lacks -- see bridge.ReadWasteTarget's doc comment) and the existing
// tend pawn read for hauler eligibility: dead, downed, drafted and mental
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
}
type RoutineWasteResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineWastePlanner(reviewer *RoutineReviewer, native RoutineWasteSource) (*RoutineWastePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineWastePlanner{reviewer, native}, nil
}
func (r *RoutineWastePlanner) Step(ctx context.Context) (RoutineWasteResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineWastePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineWasteResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineWasteResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineWasteResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineWasteResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainWaste {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineWasteResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// MaintainWaste competes for the same bounded concurrent-project capacity
	// as comfort/expansion/other priority>=3 autopilot goals; only act while
	// this review's arbitration actually selected it.
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.MaintainWaste && row.Selected
	}
	if !selected {
		return RoutineWasteResult{Reason: BuildingMethodRefused}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineWasteResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineWasteResult{Reason: BuildingMethodExistingWork}, nil
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
		return RoutineWasteResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	reading, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	items, known := reading.Projection.Facts.Waste.Value()
	if !known || len(items) == 0 {
		return RoutineWasteResult{Reason: BuildingMethodUsed}, nil
	}
	identityRef := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityRef)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineWasteResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineWasteResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identityRef, ids)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineWasteResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineWasteResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineWasteResult{}, ErrControl
	}
	var pawns []policy.WastePawn
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineWasteResult{}, ErrControl
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		pawns = append(pawns, wasteCandidateFacts(pawn, row))
	}
	item, pawn, ok := policy.SelectWasteMethod(items, pawns)
	if ok && !arbiter.tryClaim([]domain.PawnID{domain.PawnID(pawn)}, "waste-item:"+item.ID) {
		ok = false
	}
	if !ok {
		return RoutineWasteResult{Reason: BuildingMethodUsed}, nil
	}
	waste, err := domain.NewWaste(domain.PawnID(pawn), item.ID, item.Cell)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	// Keyed by item and attempt count, not pawn: a fresh attempt after an
	// interrupted or refused try picks whichever hauler is currently best.
	prefix := fmt.Sprintf("waste-%s-", item.ID)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineWasteResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-waste-%x", digest[:16]))
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
		return RoutineWasteResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineWasteResult{}, err
	}
	return RoutineWasteResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

func wasteCandidateFacts(pawn domain.PawnID, row *n.PawnState) policy.WastePawn {
	facts := policy.WastePawn{ID: policy.PawnID(pawn), Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	return facts
}
