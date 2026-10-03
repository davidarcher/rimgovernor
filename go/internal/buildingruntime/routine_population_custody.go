package buildingruntime

import (
	"context"
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
	Verdict
	Plan domain.PlanID
}

func NewRoutinePopulationCustodyPlanner(reviewer *RoutineReviewer, native RoutineRescueSource) (*RoutinePopulationCustodyPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutinePopulationCustodyPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutinePopulationCustodyPlanner{reviewer, native}, nil
}
func (r *RoutinePopulationCustodyPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePopulationCustodyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePopulationCustodyResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePopulationCustodyResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPopulation)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !workable {
		return RoutinePopulationCustodyResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutinePopulationCustodyResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := routineScope(call, r.reviewer.native)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	choice := policy.SelectCustodyMethod(read.Projection.Facts.Custody)
	arrestTarget := policy.ShrineArrestTarget(read.Projection.Facts)
	if arrestTarget != "" {
		choice = policy.CustodyChoice{Pawn: arrestTarget}
	} else if target := policy.LanceCandidate(read.Projection.Facts); target != "" {
		// A standing recruitable raider goes down alive to a lance
		// (#1038) before any capture; without an able user the step
		// goes on to capture and rescue.
		if result, ok, err := r.stepLance(call, epoch, p, state, started, goal, review.Tick, target, arbiter); err != nil || ok {
			return result, err
		}
	}
	switch choice.Reason {
	case policy.CustodyNoDeficit:
		return RoutinePopulationCustodyResult{Verdict: BuildingReasonUsed}, nil
	case policy.CustodyUnknown:
		return RoutinePopulationCustodyResult{Verdict: fieldUnavailable("custody")}, nil
	}
	identityCtx := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityCtx)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoutinePopulationCustodyResult{Verdict: BuildingReasonUsed}, nil
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
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	needed, err := plannedDrafts(call, p.journal)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	var squad []policy.ShrineDefenderFacts
	var performers []policy.RescuerFacts
	var profiles []policy.PawnProfile
	var patient *n.PawnState
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		if pawn == choice.Pawn {
			patient = row
			continue
		}
		squad = append(squad, policy.ShrineDefenderFacts{SquadDefenderFacts: squadDefenderFacts(row, needed)})
		performers = append(performers, rescue.NewRescuerFacts(pawn, row, ""))
		profiles = append(profiles, policy.BuildProfile(observation.WorkPawnRow(row)))
	}
	if patient == nil {
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: patient == nil", ErrControl)
	}
	if arrestTarget != "" {
		return r.commitArrest(call, epoch, p, state, started, goal, read.Projection.Facts, squad, arrestTarget, arbiter)
	}
	var action domain.Action
	var prefix string
	switch choice.Decision {
	case policy.CustodyRescue:
		patients := []policy.RescuePatientFacts{rescue.NewRescuePatientFacts(choice.Pawn, patient, "")}
		performer, target, ok := policy.SelectRescue(performers, patients)
		if ok && !arbiter.tryClaim([]domain.PawnID{performer, target}) {
			ok = false
		}
		if !ok {
			return RoutinePopulationCustodyResult{Verdict: BuildingReasonUsed}, nil
		}
		value, err := domain.NewRescue(performer, target)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		prefix = fmt.Sprintf("population-rescue-%s-", target)
		attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			return RoutinePopulationCustodyResult{Verdict: BuildingReasonExhausted}, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		id := domain.MintPlanID()
		action, err = domain.NewRescueAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		return r.commit(call, epoch, p, state, started, goal, method, id, action)
	case policy.CustodyCapture:
		patients := []policy.CapturePatientFacts{capture.NewCapturePatientFacts(choice.Pawn, patient, "")}
		// The warden carries the prisoner in: the combat read's biography
		// answers WardenFor, and an unavailable warden yields the ID order.
		warden, _ := policy.WardenFor(profiles, false)
		performer, target, ok := policy.SelectCapture(performers, patients, domain.PawnID(warden))
		if ok && !arbiter.tryClaim([]domain.PawnID{performer, target}) {
			ok = false
		}
		if !ok {
			return RoutinePopulationCustodyResult{Verdict: BuildingReasonUsed}, nil
		}
		value, err := domain.NewCapture(performer, target)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		prefix = fmt.Sprintf("population-capture-%s-", target)
		attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			return RoutinePopulationCustodyResult{Verdict: BuildingReasonExhausted}, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		id := domain.MintPlanID()
		action, err = domain.NewCaptureAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutinePopulationCustodyResult{}, err
		}
		return r.commit(call, epoch, p, state, started, goal, method, id, action)
	default:
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: step: check failed", ErrControl)
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
		return RoutinePopulationCustodyResult{}, fmt.Errorf("%w: commit: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	return RoutinePopulationCustodyResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
