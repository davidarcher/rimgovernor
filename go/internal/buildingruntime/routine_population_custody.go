package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/capture"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/rescue"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutinePopulationCustodyPlanner proposes one capture or rescue write for
// MaintainPopulation's custody deficit: policy.CustodyDeficit and
// SelectCustodyMethod read the same dedicated per-cycle population census
// RoutinePrisonerInteractionPlanner reads (RoutineFacts.Custody, broadened
// past prisoners alone), so no new native read is needed to detect or
// select a candidate. Performer eligibility is read the same way
// RoutineRescuePlanner reads it -- the emergency colonist pool via
// ReadCombatPawns -- since a capture/rescue performer must be an
// undrafted colonist, exactly like a CriticalMedicine rescuer.
type RoutinePopulationCustodyPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineRescueSource
}
type RoutinePopulationCustodyResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutinePopulationCustodyPlanner(reviewer *RoutineReviewer, native RoutineRescueSource) (*RoutinePopulationCustodyPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutinePopulationCustodyPlanner{reviewer, native}, nil
}
func (r *RoutinePopulationCustodyPlanner) Step(ctx context.Context) (RoutinePopulationCustodyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}
func (r *RoutinePopulationCustodyPlanner) step(call, epoch context.Context) (RoutinePopulationCustodyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePopulationCustodyResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePopulationCustodyResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainPopulation {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutinePopulationCustodyResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutinePopulationCustodyResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	read, err := observation.ObserveRoutineOwned(call, r.reviewer.native, r.reviewer.clock, expected, r.reviewer.maxAge, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	choice := policy.SelectCustodyMethod(read.Projection.Facts.Custody)
	switch choice.Reason {
	case policy.CustodyNoDeficit:
		return RoutinePopulationCustodyResult{Reason: BuildingMethodUsed}, nil
	case policy.CustodyUnknown:
		return RoutinePopulationCustodyResult{Reason: BuildingMethodUnknown}, nil
	}
	identityCtx := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityCtx)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutinePopulationCustodyResult{Reason: BuildingMethodUsed}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists)+1)
	for _, pawn := range emergency.Facts.Colonists {
		if domain.PawnID(pawn.ID) == choice.Pawn {
			continue // the custody target is never its own performer
		}
		ids = append(ids, string(pawn.ID))
	}
	ids = append(ids, string(choice.Pawn))
	reply, _, err := r.native.ReadCombatPawns(call, identityCtx, ids)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(ids)) || counts.GetReturned() != uint64(len(ids)) || len(observed.Pawns) != len(ids) {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	var performers []policy.RescuerFacts
	var patient *n.PawnState
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutinePopulationCustodyResult{}, ErrControl
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		if pawn == choice.Pawn {
			patient = row
			continue
		}
		performers = append(performers, rescue.NewRescuerFacts(pawn, row, ""))
	}
	if patient == nil {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	var action domain.Action
	var prefix string
	switch choice.Decision {
	case policy.CustodyRescue:
		patients := []policy.RescuePatientFacts{rescue.NewRescuePatientFacts(choice.Pawn, patient, "")}
		performer, target, ok := policy.SelectRescue(performers, patients)
		if !ok {
			return RoutinePopulationCustodyResult{Reason: BuildingMethodUsed}, nil
		}
		value, err := domain.NewRescue(performer, target)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		prefix = fmt.Sprintf("population-rescue-%s-", target)
		attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			return RoutinePopulationCustodyResult{Reason: BuildingMethodExhausted}, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
		id := domain.PlanID(fmt.Sprintf("routine-population-custody-%x", digest[:16]))
		action, err = domain.NewRescueAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		return r.commit(call, epoch, p, state, started, goal, method, id, action)
	case policy.CustodyCapture:
		patients := []policy.CapturePatientFacts{capture.NewCapturePatientFacts(choice.Pawn, patient, "")}
		performer, target, ok := policy.SelectCapture(performers, patients)
		if !ok {
			return RoutinePopulationCustodyResult{Reason: BuildingMethodUsed}, nil
		}
		value, err := domain.NewCapture(performer, target)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		prefix = fmt.Sprintf("population-capture-%s-", target)
		attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			return RoutinePopulationCustodyResult{Reason: BuildingMethodExhausted}, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
		id := domain.PlanID(fmt.Sprintf("routine-population-custody-%x", digest[:16]))
		action, err = domain.NewCaptureAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		return r.commit(call, epoch, p, state, started, goal, method, id, action)
	default:
		return RoutinePopulationCustodyResult{}, ErrControl
	}
}

func (r *RoutinePopulationCustodyPlanner) commit(call, epoch context.Context, p *Player, state ControlState, started time.Time, goal store.GoalState, method domain.MethodID, id domain.PlanID, action domain.Action) (RoutinePopulationCustodyResult, error) {
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationCustodyResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	return RoutinePopulationCustodyResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
