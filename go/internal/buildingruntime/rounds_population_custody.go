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

// RoundsPopulationCustodyPlanner proposes one capture or rescue write for
// MaintainPopulation's custody deficit: policy.CustodyDeficit and
// SelectCustodyMethod read the same dedicated per-cycle population census
// RoundsPrisonerInteractionPlanner reads (RoundsFacts.Custody, broadened
// past prisoners alone), so no new native read is needed to detect or
// select a candidate. Performer eligibility is read the same way
// RoundsRescuePlanner reads it -- the emergency colonist pool via
// ReadCombatPawns -- since a capture/rescue performer must be an
// undrafted colonist, exactly like a CriticalMedicine rescuer.
type RoundsPopulationCustodyPlanner struct {
	reviewer *Rounder
	native   RoundsCustodySource
}
type RoundsPopulationCustodyResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoundsPopulationCustodyPlanner(reviewer *Rounder, native RoundsCustodySource) (*RoundsPopulationCustodyPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsPopulationCustodyPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsPopulationCustodyPlanner{reviewer, native}, nil
}
func (r *RoundsPopulationCustodyPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsPopulationCustodyResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsPopulationCustodyResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsPopulationCustodyResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPopulation)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	if !workable {
		return RoundsPopulationCustodyResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsPopulationCustodyResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
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
	if choice.Reason == policy.CustodyNoDeficit {
		// A downed entity the capture rule (#1742) takes goes to a holding
		// platform through the same capture order.
		if entity, ok := policy.EntityCaptureTarget(read.Projection.Facts.Containment); ok {
			choice = policy.CustodyChoice{Pawn: entity, Decision: policy.CustodyCapture}
		}
	}
	if choice.Reason == policy.CustodyNoDeficit {
		// No custody work stands: a held entity's cell door and wounds are
		// the upkeep left (#1743).
		return r.stepContainment(call, epoch, p, state, started, goal, read.Projection.Facts.Containment, read.Projection.Facts.Research, arbiter)
	}
	switch choice.Reason {
	case policy.CustodyNoDeficit:
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "custody_deficit")}, nil
	case policy.CustodyUnknown:
		return RoundsPopulationCustodyResult{Verdict: fieldUnavailable("custody")}, nil
	}
	identityCtx := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identityCtx)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "colonists_complete")}, nil
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
		return RoundsPopulationCustodyResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	needed, err := plannedDrafts(call, p.journal)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	arms, err := readArmament(call, r.native, identityCtx)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	catalog := arms.catalog
	var squad []policy.ShrineDefenderFacts
	var performers []policy.RescuerFacts
	var profiles []policy.PawnProfile
	var patient *n.PawnState
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		if pawn == choice.Pawn {
			patient = row
			continue
		}
		ranged, err := rangedWeaponEquipped(row.Equipment, arms)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		squad = append(squad, policy.ShrineDefenderFacts{SquadDefenderFacts: squadDefenderFacts(row, needed, ranged)})
		performers = append(performers, rescue.NewRescuerFacts(pawn, row, ""))
		work, err := observation.WorkPawnRow(row, catalog, arms.things)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		profiles = append(profiles, policy.BuildProfile(work))
	}
	if patient == nil {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: patient == nil", ErrControl)
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
			return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "rescue_claim")}, nil
		}
		value, err := domain.NewRescue(performer, target)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		prefix = fmt.Sprintf("population-rescue-%s-", target)
		attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			return RoundsPopulationCustodyResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		id := domain.MintPlanID()
		action, err = domain.NewRescueAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
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
			return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "capture_claim")}, nil
		}
		value, err := domain.NewCapture(performer, target)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		prefix = fmt.Sprintf("population-capture-%s-", target)
		attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			return RoundsPopulationCustodyResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		id := domain.MintPlanID()
		action, err = domain.NewCaptureAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		return r.commit(call, epoch, p, state, started, goal, method, id, action)
	default:
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: step: check failed", ErrControl)
	}
}

func (r *RoundsPopulationCustodyPlanner) commit(call, epoch context.Context, p *Player, state ControlState, started time.Time, goal store.StandardState, method domain.MethodID, id domain.PlanID, action domain.Action) (RoundsPopulationCustodyResult, error) {
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: commit: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	return RoundsPopulationCustodyResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
