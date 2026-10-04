package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// reviewRituals fills the reading's ritual plans (#1660): the rituals due and
// ready to begin (policy.PlanRituals over the ideoligion, the pawn rows and
// the building sites), planned only while the emergency census reads calm.
// Both the review and the planners' own readings carry them, so the schedule
// planners hold the same attendees off Sleep that the planner gathers.
func (r *Rounder) reviewRituals(reading *observation.RoutineReading, snapshot domain.GenerationSnapshot) {
	facts := &reading.Projection.Facts
	tick := reading.Projection.Identity.Tick
	calm := domain.Unknown[bool]()
	if emergency, err := policy.NewEmergencySnapshot(snapshot, tick, reading.Emergency); err == nil {
		hostiles, patients := policy.EmergencyNeeds(emergency, snapshot, tick)
		calm = policy.RitualCalm(hostiles, patients)
	}
	facts.RitualPlans = policy.PlanRituals(facts.Ideology, reading.Projection.WorkPawns, facts.RitualSites, tick, calm)
	facts.RitualsOwed = policy.RitualsOwed(facts.RitualPlans)
}

// RoutineRitualsPlanner is MaintainRituals' planner (#1660, epic #1653): while
// a ritual is due and ready (policy.PlanRituals), it commits one Ritual
// `begin` for it: the organizer, the spot, the role slots and the spectators.
// The committed plan is the persisted intent on the goal's method. The game
// offers the begin command at the building its obligation targets, so a
// refused begin is tried at the next site; each ritual and site is tried at
// most maxMedicalAttemptsPerPatient times per Episode.
type RoutineRitualsPlanner struct {
	reviewer *Rounder
}
type RoutineRitualsResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutineRitualsPlanner(reviewer *Rounder) (*RoutineRitualsPlanner, error) {
	if reviewer == nil || !reviewer.methodEnabled(policy.MaintainRituals) {
		return nil, fmt.Errorf("%w: NewRoutineRitualsPlanner: reviewer == nil || !reviewer.methodEnabled(policy.MaintainRituals)", ErrControl)
	}
	return &RoutineRitualsPlanner{reviewer}, nil
}

func (r *RoutineRitualsPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineRitualsResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineRitualsResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineRitualsResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineRitualsResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineRitualsResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainRituals)
	if err != nil {
		return RoutineRitualsResult{}, err
	}
	if !workable {
		return RoutineRitualsResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineRitualsResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineRitualsResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineRitualsResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineRitualsResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineRitualsResult{}, err
	}
	plans, known := read.Projection.Facts.RitualPlans.Value()
	if !known || len(plans) == 0 {
		return RoutineRitualsResult{Verdict: waitFor(WaitMethodUsed, "ritual_plans")}, nil
	}
	// The first ritual and site whose attempts are not spent; one ritual
	// begins per step.
	for _, plan := range plans {
		for _, site := range plan.Sites {
			prefix := fmt.Sprintf("ritual-%s-%d-%d-", plan.Ritual, site.X, site.Z)
			attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
			if attempt >= maxMedicalAttemptsPerPatient {
				continue
			}
			attendees := make([]domain.PawnID, 0, 8)
			for _, pawn := range plan.Attendees() {
				attendees = append(attendees, domain.PawnID(pawn))
			}
			if !arbiter.tryClaim(attendees) {
				return RoutineRitualsResult{Verdict: claimHeld("attendees")}, nil
			}
			slots := make([]domain.RitualSlot, 0, len(plan.Slots))
			for _, slot := range plan.Slots {
				fill := domain.RitualSlot{Slot: slot.Slot}
				for _, pawn := range slot.Pawns {
					fill.Pawns = append(fill.Pawns, domain.PawnID(pawn))
				}
				slots = append(slots, fill)
			}
			spectators := make([]domain.PawnID, 0, len(plan.Spectators))
			for _, pawn := range plan.Spectators {
				spectators = append(spectators, domain.PawnID(pawn))
			}
			ritual, err := domain.NewRitualBegin(domain.PawnID(plan.Organizer), plan.Ritual, site, slots, spectators)
			if err != nil {
				return RoutineRitualsResult{}, err
			}
			id := domain.MintPlanID()
			action, err := domain.NewRitualAction(domain.ActionID(fmt.Sprintf("%s-0", id)), ritual)
			if err != nil {
				return RoutineRitualsResult{}, err
			}
			spec, err := domain.NewPlan(id, 1, []domain.Action{action})
			if err != nil {
				return RoutineRitualsResult{}, err
			}
			if err = p.current(call, epoch); err != nil {
				return RoutineRitualsResult{}, err
			}
			elapsed := r.reviewer.clock.Now().Sub(started)
			if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
				return RoutineRitualsResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
			}
			method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
			reason := fmt.Sprintf("ideology: %s leads %s at (%d,%d) with %d attending", plan.Organizer, plan.Def, site.X, site.Z, len(attendees))
			if _, err = p.journal.CommitMethodReason(call, goal.Standard.ID, goal.Revision, method, reason, spec); err != nil {
				return RoutineRitualsResult{}, err
			}
			return RoutineRitualsResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
		}
	}
	return RoutineRitualsResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
}
