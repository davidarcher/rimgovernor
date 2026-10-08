package buildingruntime

import (
	"context"
	"fmt"
	"log/slog"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/tend"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsCustodySource is what the custody planner reads: the rescue
// planner's reads and the tend planner's, since a held entity is tended by
// the same doctor selection a colonist is (#1743).
type RoundsCustodySource interface {
	RoundsRescueSource
	RoundsTendSource
}

// stepContainment is the custody step's containment upkeep (#1743), reached
// when no prisoner, capture or rescue work stands: a held entity's cell door
// held open is closed (the combat door CLOSE order, as one close_door
// action), then a held entity whose bioferrite pays has its extract flag set
// (#2434, one extract_bioferrite pawn setting; the game's Doctor work then
// runs the job), then a held entity that needs tending is tended through the same
// SelectTend a colonist patient takes. Facts upkeep cannot read, and a door
// no order can clear, are logged loudly and are not a pass.
func (r *RoundsPopulationCustodyPlanner) stepContainment(call, epoch context.Context, p *Player, state ControlState, started time.Time, goal store.StandardState, facts policy.RoundsFacts, arbiter *stepArbiter) (RoundsPopulationCustodyResult, error) {
	containment, research := facts.Containment, facts.Research
	upkeep := policy.ContainmentDoorUpkeep(containment)
	for _, issue := range upkeep.Issues {
		defenseAction(call, "routine-population-custody", slog.LevelWarn, "refused", "containment_upkeep_issue", fmt.Sprintf("%d,%d", issue.Cell.X, issue.Cell.Z), map[string]any{"x": issue.Cell.X, "z": issue.Cell.Z, "detail": issue.Reason})
	}
	if len(upkeep.CloseDoors) > 0 {
		cell := upkeep.CloseDoors[0]
		door, err := domain.NewCloseDoor(cell)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		prefix := fmt.Sprintf("population-door-%d-%d-", cell.X, cell.Z)
		attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			defenseAction(call, "routine-population-custody", slog.LevelWarn, "refused", "containment_upkeep_exhausted", fmt.Sprintf("%d,%d", cell.X, cell.Z), map[string]any{"x": cell.X, "z": cell.Z})
			return RoundsPopulationCustodyResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		id := domain.MintPlanID()
		action, err := domain.NewCloseDoorAction(domain.ActionID(fmt.Sprintf("%s-0", id)), door)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		return r.commit(call, epoch, p, state, started, goal, method, id, action)
	}
	harvest := policy.BioferriteHarvestOwed(containment, research)
	for _, reason := range harvest.Issues {
		defenseAction(call, "routine-population-custody", slog.LevelWarn, "refused", "bioferrite_harvest_unread", "bioferrite", map[string]any{"detail": reason})
	}
	if len(harvest.Enable) > 0 {
		entity := harvest.Enable[0]
		prefix := fmt.Sprintf("population-bioferrite-%s-", entity)
		attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			defenseAction(call, "routine-population-custody", slog.LevelWarn, "refused", "bioferrite_harvest_exhausted", string(entity), nil)
			return RoundsPopulationCustodyResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
		}
		setting, err := domain.NewExtractBioferriteSetting(entity, true)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewPawnSettingsAction(domain.ActionID(fmt.Sprintf("%s-0", id)), setting)
		if err != nil {
			return RoundsPopulationCustodyResult{}, err
		}
		return r.commit(call, epoch, p, state, started, goal, domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt)), id, action)
	}
	if result, ok, err := r.stepMonolith(call, epoch, p, state, started, goal, facts, arbiter); err != nil || ok {
		return result, err
	}
	patient, ok := policy.EntityTendTarget(containment)
	if !ok {
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "entity_tend_target")}, nil
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	identity := boundary.Identity(state.Snapshot)
	emergency, _, err := r.native.ReadEmergency(call, identity)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil || emergency.Context.GetTick() < int64(review.Tick) {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: stepContainment: emergency read outside the review", ErrControl)
	}
	complete, known := emergency.Facts.ColonistsComplete.Value()
	if !known || !complete || len(emergency.Facts.Colonists) == 0 {
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "colonists_complete")}, nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists)+1)
	for _, pawn := range emergency.Facts.Colonists {
		if domain.PawnID(pawn.ID) != patient {
			ids = append(ids, string(pawn.ID))
		}
	}
	ids = append(ids, string(patient))
	reply, _, err := r.native.ReadTendPawns(call, identity, ids)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: stepContainment: observed == nil", ErrControl)
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: stepContainment: tend read outside the world", ErrControl)
	}
	if len(observed.Pawns) != len(ids) {
		return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: stepContainment: %d tend rows for %d pawns", ErrControl, len(observed.Pawns), len(ids))
	}
	var doctors []policy.TendDoctorFacts
	var patients []policy.TendPatientFacts
	seen := map[string]bool{}
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || seen[row.Pawn.GetId()] {
			return RoundsPopulationCustodyResult{}, fmt.Errorf("%w: stepContainment: tend row missing or repeated", ErrControl)
		}
		seen[row.Pawn.GetId()] = true
		pawn := domain.PawnID(row.Pawn.GetId())
		if pawn == patient {
			patients = append(patients, tend.NewTendPatientFacts(pawn, row, ""))
			continue
		}
		doctors = append(doctors, tend.NewTendDoctorFacts(pawn, row, ""))
	}
	doctor, target, ok := policy.SelectTend(doctors, patients, tend.TendReachability(observed.Pawns))
	if ok && !arbiter.tryClaim([]domain.PawnID{doctor, target}) {
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "pawn_claim")}, nil
	}
	if !ok {
		defenseAction(call, "routine-population-custody", slog.LevelWarn, "waiting", "entity_tend_unavailable", string(patient), nil)
		return RoundsPopulationCustodyResult{Verdict: waitFor(WaitMethodUsed, "entity_tend_doctor")}, nil
	}
	value, err := domain.NewTend(doctor, target)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	prefix := fmt.Sprintf("population-tend-%s-", target)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPopulationCustodyResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	action, err := domain.NewTendAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	return r.commit(call, epoch, p, state, started, goal, method, id, action)
}
