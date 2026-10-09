package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/rescue"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoundsRescueSource is intentionally the plain combat pawn read (no work or
// care details): rescue eligibility only needs identity/health/job facts.
type RoundsRescueSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}
type RoundsRescuePlanner struct {
	reviewer *Rounder
	native   RoundsRescueSource
}
type RoundsRescueResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks is a bounded window the step may lend when the
	// CriticalMedical deficit stands but no rescue method can run
	// (medicalWaitTicks).
	NativeWorkTicks uint32
}

func NewRoundsRescuePlanner(reviewer *Rounder, native RoundsRescueSource) (*RoundsRescuePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsRescuePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsRescuePlanner{reviewer, native}, nil
}
func (r *RoundsRescuePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsRescueResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsRescueResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsRescueResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsRescueResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsRescueResult{Verdict: BuildingReasonNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.CriticalMedicine)
	if err != nil {
		return RoundsRescueResult{}, err
	}
	if !found {
		return RoundsRescueResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
		return RoundsRescueResult{Verdict: BuildingReasonExistingWork}, err
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoundsRescueResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoundsRescueResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoundsRescueResult{Verdict: waitFor(WaitMethodUsed, "colonist_census")}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadCombatPawns(call, identity, ids)
	if err != nil {
		return RoundsRescueResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsRescueResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsRescueResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoundsRescueResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	var rescuers []policy.RescuerFacts
	var patients []policy.RescuePatientFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoundsRescueResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
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
		// No pair to order: only game time frees a rescuer or resolves
		// the casualty, so the step lends a window.
		return RoundsRescueResult{Verdict: waitFor(WaitMethodUsed, "rescue_pairing"), NativeWorkTicks: medicalWaitTicks}, nil
	}
	rescue, err := domain.NewRescue(rescuer, patient)
	if err != nil {
		return RoundsRescueResult{}, err
	}
	// Keyed by patient and attempt count, not rescuer: a fresh attempt after
	// an interrupted or failed try picks whichever rescuer is currently best,
	// which is how rescuer replacement happens across cycles.
	prefix := fmt.Sprintf("rescue-%s-", patient)
	attempt := incidentAttemptCount(incident.Methods, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		// The attempts are spent and the deficit stays visible; the
		// clock must still advance under it.
		return RoundsRescueResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", ""), NativeWorkTicks: medicalWaitTicks}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	action, err := domain.NewRescueAction(domain.ActionID(fmt.Sprintf("%s-0", id)), rescue)
	if err != nil {
		return RoundsRescueResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsRescueResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsRescueResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsRescueResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoundsRescueResult{}, err
	}
	return RoundsRescueResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
