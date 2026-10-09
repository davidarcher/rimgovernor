package buildingruntime

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/tend"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"sort"
	"time"
)

func (r *RoundsPopulationJoinerPlanner) admitRefugeeTend(call, epoch context.Context, state ControlState, goal store.StandardState, review store.Rounds, read observation.RoundsReading, arbiter *stepArbiter, started time.Time) (RoundsPopulationJoinerResult, bool, error) {
	refugees := policy.QuestRefugeeIDs(read.Projection.Facts.QuestOffers)
	if len(refugees) == 0 {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	housed := map[domain.PawnID]bool{}
	if rows, known := read.Projection.Facts.Custody.Value(); known {
		for _, row := range rows {
			guest, gk := row.Guest.Value()
			prisoner, pk := row.Prisoner.Value()
			admitted, ak := row.Admitted.Value()
			housed[row.Pawn] = gk && guest || pk && prisoner || ak && admitted
		}
	}
	native, ok := r.reviewer.native.(RoundsTendSource)
	if !ok {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := native.ReadEmergency(call, identity)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoundsPopulationJoinerResult{}, false, ErrControl
	}
	if complete, known := emergency.Facts.ColonistsComplete.Value(); !known || !complete {
		return RoundsPopulationJoinerResult{}, false, nil
	}
	ids := []string{}
	seen := map[domain.PawnID]bool{}
	for _, pawn := range emergency.Facts.Colonists {
		id := domain.PawnID(pawn.ID)
		seen[id] = true
		ids = append(ids, string(id))
	}
	for id := range refugees {
		if !seen[id] {
			ids = append(ids, string(id))
		}
	}
	sort.Strings(ids)
	reply, _, err := native.ReadTendPawns(call, identity, ids)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	observed := reply.GetObserved()
	if observed == nil || len(observed.Pawns) != len(ids) {
		return RoundsPopulationJoinerResult{}, false, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	var doctors []policy.TendDoctorFacts
	var patients []policy.TendPatientFacts
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil {
			return RoundsPopulationJoinerResult{}, false, ErrControl
		}
		id := domain.PawnID(row.Pawn.GetId())
		if refugees[id] != "" && housed[id] {
			patients = append(patients, tend.NewTendPatientFacts(id, row, ""))
		} else if seen[id] && refugees[id] == "" {
			doctors = append(doctors, tend.NewTendDoctorFacts(id, row, ""))
		}
	}
	doctor, patient, ok := policy.SelectTend(doctors, patients, tend.TendReachability(observed.Pawns))
	if !ok {
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitMethodUsed, "refugee_care"), NativeWorkTicks: medicalWaitTicks}, false, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{doctor, patient}) {
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitClaim, "refugee_care")}, true, nil
	}
	value, err := domain.NewTend(doctor, patient)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	prefix := fmt.Sprintf("quest-refugee-tend-%s-", patient)
	method, verdict, admitted, err := admitStandardMethod(call, r.reviewer.player.journal, goal, prefix, state.Snapshot)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if !admitted {
		return RoundsPopulationJoinerResult{Verdict: verdict, NativeWorkTicks: medicalWaitTicks}, false, nil
	}
	id := domain.MintPlanID()
	action, err := domain.NewTendAction(domain.ActionID(string(id)+"-0"), value)
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	if err = r.reviewer.player.current(call, epoch); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if r.reviewer.player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPopulationJoinerResult{}, false, ErrControl
	}
	if _, err = r.reviewer.player.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationJoinerResult{}, false, err
	}
	return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id, NativeWorkTicks: medicalWaitTicks}, true, nil
}
