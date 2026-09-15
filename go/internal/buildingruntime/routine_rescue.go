package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/rescue"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineRescueSource is intentionally the plain combat pawn read (no work or
// care details): rescue eligibility only needs identity/health/job facts.
type RoutineRescueSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}
type RoutineRescuePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineRescueSource
}
type RoutineRescueResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineRescuePlanner(reviewer *RoutineReviewer, native RoutineRescueSource) (*RoutineRescuePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineRescuePlanner{reviewer, native}, nil
}
func (r *RoutineRescuePlanner) Step(ctx context.Context) (RoutineRescueResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineRescueResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineRescuePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineRescueResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineRescueResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineRescueResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineRescueResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineRescueResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.CriticalMedicine {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineRescueResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineRescueResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineRescueResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineRescueResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoutineRescueResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineRescueResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineRescueResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadCombatPawns(call, identity, ids)
	if err != nil {
		return RoutineRescueResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineRescueResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineRescueResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutineRescueResult{}, ErrControl
	}
	var rescuers []policy.RescuerFacts
	var patients []policy.RescuePatientFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineRescueResult{}, ErrControl
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		rescuers = append(rescuers, rescue.NewRescuerFacts(pawn, row, ""))
		patients = append(patients, rescue.NewRescuePatientFacts(pawn, row, ""))
	}
	rescuer, patient, ok := policy.SelectRescue(rescuers, patients)
	if ok && !arbiter.tryClaim([]domain.PawnID{rescuer, patient}) {
		ok = false
	}
	if !ok {
		return RoutineRescueResult{Reason: BuildingMethodUsed}, nil
	}
	rescue, err := domain.NewRescue(rescuer, patient)
	if err != nil {
		return RoutineRescueResult{}, err
	}
	// Keyed by patient and attempt count, not rescuer: a fresh attempt after
	// an interrupted or failed try picks whichever rescuer is currently best,
	// which is how rescuer replacement happens across cycles.
	prefix := fmt.Sprintf("rescue-%s-", patient)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineRescueResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-rescue-%x", digest[:16]))
	action, err := domain.NewRescueAction(domain.ActionID(fmt.Sprintf("%s-0", id)), rescue)
	if err != nil {
		return RoutineRescueResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineRescueResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineRescueResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineRescueResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineRescueResult{}, err
	}
	return RoutineRescueResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
