package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/tend"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type RoundsTendSource interface {
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadTendPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}
type RoundsTendPlanner struct {
	reviewer *Rounder
	native   RoundsTendSource
}
type RoundsTendResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks is a bounded window the step may lend when the
	// CriticalMedical deficit stands but no tend method can run
	// (medicalWaitTicks).
	NativeWorkTicks uint32
}

func NewRoundsTendPlanner(reviewer *Rounder, native RoundsTendSource) (*RoundsTendPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsTendPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsTendPlanner{reviewer, native}, nil
}
func (r *RoundsTendPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsTendResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsTendResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsTendResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsTendResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsTendResult{Verdict: BuildingReasonNoReview}, nil
	}
	incident, found, err := incidentDeficit(call, p.journal, review, policy.CriticalMedicine)
	if err != nil {
		return RoundsTendResult{}, err
	}
	if !found {
		return RoundsTendResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
		return RoundsTendResult{Verdict: BuildingReasonExistingWork}, err
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoundsTendResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoundsTendResult{}, fmt.Errorf("%w: step: err != nil || emergency.Context.GetTick() < int64(review.Tick)", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoundsTendResult{Verdict: waitFor(WaitMethodUsed, "colonist_census")}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	reply, _, err := r.native.ReadTendPawns(call, identity, ids)
	if err != nil {
		return RoundsTendResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsTendResult{}, fmt.Errorf("%w: step: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsTendResult{}, fmt.Errorf("%w: step: err != nil", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoundsTendResult{}, fmt.Errorf("%w: step: len(observed.Pawns) != len(ids)", ErrControl)
	}
	var doctors []policy.TendDoctorFacts
	var patients []policy.TendPatientFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoundsTendResult{}, fmt.Errorf("%w: step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]", ErrControl)
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
		// the step lends a window rather than reporting no work.
		return RoundsTendResult{Verdict: waitFor(WaitMethodUsed, "medical_pair"), NativeWorkTicks: medicalWaitTicks}, nil
	}
	tend, err := domain.NewTend(doctor, patient)
	if err != nil {
		return RoundsTendResult{}, err
	}
	// Keyed by patient and attempt count, not doctor: a fresh attempt after an
	// interrupted or failed try picks whichever doctor is currently best,
	// which is how doctor replacement happens across cycles.
	prefix := fmt.Sprintf("tend-%s-", patient)
	if verdict, ok, err := admitSubject(call, p.journal, prefix, incidentPlans(incident.Methods, prefix), state.Snapshot); err != nil {
		return RoundsTendResult{}, err
	} else if !ok {
		// Native refused this patient's treatment and the refusal still
		// stands; the deficit stays visible and the clock must still advance
		// under it.
		return RoundsTendResult{Verdict: verdict, NativeWorkTicks: medicalWaitTicks}, nil
	}
	method := nextMethodID(prefix, incidentMethodIDs(incident.Methods))
	id := domain.MintPlanID()
	action, err := domain.NewTendAction(domain.ActionID(fmt.Sprintf("%s-0", id)), tend)
	if err != nil {
		return RoundsTendResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsTendResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsTendResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsTendResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
		return RoundsTendResult{}, err
	}
	return RoundsTendResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
