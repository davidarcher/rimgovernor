package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/tend"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type RoutineTendSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}
type RoutineTendPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineTendSource
}
type RoutineTendResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// NativeWorkTicks is a bounded window the step may lend when the
	// CriticalMedical deficit stands but no tend method can run
	// (medicalWaitTicks, #636).
	NativeWorkTicks uint32
}

func NewRoutineTendPlanner(reviewer *RoutineReviewer, native RoutineTendSource) (*RoutineTendPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineTendPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineTendPlanner{reviewer, native}, nil
}
func (r *RoutineTendPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineTendResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineTendResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineTendResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineTendResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineTendResult{Reason: BuildingMethodNoReview}, nil
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
		return RoutineTendResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineTendResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineTendResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineTendResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoutineTendResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutineTendResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutineTendResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identity, ids)
	if err != nil {
		return RoutineTendResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineTendResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutineTendResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoutineTendResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	var doctors []policy.TendDoctorFacts
	var patients []policy.TendPatientFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutineTendResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		doctors = append(doctors, tend.NewTendDoctorFacts(pawn, row, ""))
		patients = append(patients, tend.NewTendPatientFacts(pawn, row, ""))
	}
	doctor, patient, ok := policy.SelectTend(doctors, patients, tend.TendReachability(observed.Pawns))
	if ok && !arbiter.tryClaim([]domain.PawnID{doctor, patient}) {
		ok = false
	}
	if !ok {
		// No pair to order: the patient is up and out of bed, or every
		// doctor is ineligible, busy or walled off from the patients. Only game time changes that, so
		// the step lends a window rather than reporting no work (#636).
		return RoutineTendResult{Reason: BuildingMethodUsed, NativeWorkTicks: medicalWaitTicks}, nil
	}
	tend, err := domain.NewTend(doctor, patient)
	if err != nil {
		return RoutineTendResult{}, err
	}
	// Keyed by patient and attempt count, not doctor: a fresh attempt after an
	// interrupted or failed try picks whichever doctor is currently best,
	// which is how doctor replacement happens across cycles.
	prefix := fmt.Sprintf("tend-%s-", patient)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		// The attempts are spent and the deficit stays visible; the
		// clock must still advance under it (#636).
		return RoutineTendResult{Reason: BuildingMethodExhausted, NativeWorkTicks: medicalWaitTicks}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID("routine-tend")
	action, err := domain.NewTendAction(domain.ActionID(fmt.Sprintf("%s-0", id)), tend)
	if err != nil {
		return RoutineTendResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineTendResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineTendResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineTendResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineTendResult{}, err
	}
	return RoutineTendResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
