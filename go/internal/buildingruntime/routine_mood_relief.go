package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RoutineMoodReliefPlanner struct {
	reviewer *RoutineReviewer
}
type RoutineMoodReliefResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineMoodReliefPlanner(reviewer *RoutineReviewer) (*RoutineMoodReliefPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutineMoodReliefPlanner: reviewer == nil", ErrControl)
	}
	return &RoutineMoodReliefPlanner{reviewer}, nil
}

// moodReliefValue mirrors store's unexported moodFact/moodValue lift for
// the review's persisted RoutineMoodPawn/RoutineMoodCause pointer fields;
// duplicated here (rather than exported from store) the same way
// medicalOptionalBool/medicalOptionalTicks duplicate observation's lift for
// RoutineMedicalPlanner's own fresh census.
func moodReliefValue[T any](v *T) domain.Fact[T] {
	if v == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*v)
}

func moodReliefPolicyState(s store.RoutineMoodState) policy.MoodState {
	p := s.Pawn
	row := policy.MoodPawn{ID: p.ID, Mood: moodReliefValue(p.Mood), Threshold: moodReliefValue(p.Threshold), Target: moodReliefValue(p.Target), Food: moodReliefValue(p.Food), Rest: moodReliefValue(p.Rest), Joy: moodReliefValue(p.Joy), Mental: moodReliefValue(p.Mental), Dead: moodReliefValue(p.Dead), Downed: moodReliefValue(p.Downed), Drafted: moodReliefValue(p.Drafted), PlayerForced: moodReliefValue(p.PlayerForced)}
	state := policy.MoodState{Pawn: row, Active: s.Active, Missing: s.Missing, MentalRisk: s.MentalRisk, Provision: append([]policy.MoodProvision(nil), s.Provision...), Unowned: append([]policy.MoodThought(nil), s.Unowned...)}
	for _, cause := range s.Causes {
		state.Causes = append(state.Causes, policy.MoodCause{Need: cause.Need, Level: moodReliefValue(cause.Level)})
	}
	return state
}

func domainMoodReliefNeed(need policy.MoodNeed) (domain.MoodReliefNeed, bool) {
	switch need {
	case policy.MoodFood:
		return domain.MoodReliefFood, true
	case policy.MoodRest:
		return domain.MoodReliefRest, true
	case policy.MoodJoy:
		return domain.MoodReliefJoy, true
	default:
		return "", false
	}
}

// moodReliefUsedNeeds reports needs whose prior committed attempts (this
// pawn's occurrence) already reached maxMedicalAttemptsPerPatient: passing
// these to policy.SelectMoodMethod lets it move on to the pawn's next
// measured cause instead of proposing an exhausted one again, mirroring how
// RoutineHusbandryPlanner keys attempts by method to avoid cross-method
// collisions (3a6b30b8).
func moodReliefUsedNeeds(methods []store.IncidentMethod, pawn policy.PawnID) []policy.MoodNeed {
	var used []policy.MoodNeed
	for _, need := range []policy.MoodNeed{policy.MoodFood, policy.MoodRest, policy.MoodJoy} {
		prefix := fmt.Sprintf("mood-%s-%s-", need, pawn)
		if incidentAttemptCount(methods, prefix) >= maxMedicalAttemptsPerPatient {
			used = append(used, need)
		}
	}
	return used
}

func (r *RoutineMoodReliefPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineMoodReliefResult, error) {
	p := r.reviewer.player
	sessionState := p.session.State()
	if !sessionState.Enabled {
		return RoutineMoodReliefResult{Reason: BuildingMethodDisabled}, nil
	}
	if !sessionState.ObservationKnown || sessionState.Snapshot.Validate() != nil || sessionState.Snapshot.Native == 0 {
		return RoutineMoodReliefResult{}, fmt.Errorf("%w: step: !sessionState.ObservationKnown || sessionState.Snapshot.Validate() != nil || sessionState.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineMoodReliefResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(sessionState.Snapshot) {
		return RoutineMoodReliefResult{Reason: BuildingMethodNoReview}, nil
	}
	if review.Mood == nil || len(review.Mood.States) == 0 {
		return RoutineMoodReliefResult{Reason: BuildingMethodUsed}, nil
	}
	statesByPawn := map[domain.PawnID]store.RoutineMoodState{}
	for _, s := range review.Mood.States {
		statesByPawn[domain.PawnID(s.Pawn.ID)] = s
	}
	// Owner goals a provisioning proposal can defer to: bound by this review
	// and still active with a deficit.
	activeOwner := map[domain.GoalID]bool{}
	for _, binding := range review.Goals {
		if !policy.MoodProvisionGoal(binding.Need) {
			continue
		}
		goal, err := p.journal.LoadGoal(call, binding.Goal)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		activeOwner[binding.Need] = goal.Goal.Status == domain.GoalActive && goal.Goal.Need == domain.NeedDeficit
	}
	started := r.reviewer.clock.Now()
	for _, binding := range review.SubjectIncidents(policy.EnsureMood) {
		if binding.Need != domain.NeedDeficit {
			continue
		}
		incident, err := p.journal.LoadIncident(call, binding.Incident)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		if review.VetoIncident(incident.Incident) != "" {
			continue
		}
		if open, err := incidentOpenWork(call, p.journal, incident); err != nil || open {
			return RoutineMoodReliefResult{Reason: BuildingMethodExistingWork}, err
		}
		moodState, found := statesByPawn[binding.Subject]
		if !found {
			continue
		}
		policyState := moodReliefPolicyState(moodState)
		used := moodReliefUsedNeeds(incident.Methods, moodState.Pawn.ID)
		proposal, err := policy.SelectMoodMethod(policyState, used)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		provisioning := false
		for _, owner := range policyState.Provision {
			provisioning = provisioning || activeOwner[owner.Goal]
		}
		if proposal.Reason == policy.MoodProvisioned && !provisioning {
			// No active upkeep goal owns any facility the pressure names
			// (their own censuses report them recovered or unknown), so
			// nothing is being provisioned: fall back to measured need relief.
			proposal, err = policy.SelectMoodMethod(policyState.WithoutProvision(), used)
			if err != nil {
				return RoutineMoodReliefResult{}, err
			}
		}
		if proposal.Reason != policy.MoodRelief {
			continue
		}
		need, ok := domainMoodReliefNeed(proposal.Need)
		if !ok {
			return RoutineMoodReliefResult{}, fmt.Errorf("%w: step: !ok", ErrControl)
		}
		prefix := fmt.Sprintf("mood-%s-%s-", proposal.Need, moodState.Pawn.ID)
		attempt := incidentAttemptCount(incident.Methods, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			continue
		}
		if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(moodState.Pawn.ID)}) {
			continue
		}
		relief, err := domain.NewMoodRelief(domain.PawnID(moodState.Pawn.ID), need)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		id := domain.MintPlanID()
		action, err := domain.NewMoodReliefAction(domain.ActionID(fmt.Sprintf("%s-0", id)), relief)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		if err = p.current(call, epoch); err != nil {
			return RoutineMoodReliefResult{}, err
		}
		elapsed := r.reviewer.clock.Now().Sub(started)
		if p.session.State() != sessionState || elapsed < 0 || elapsed > r.reviewer.maxAge {
			return RoutineMoodReliefResult{}, fmt.Errorf("%w: step: p.session.State() != sessionState || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		if _, err = p.journal.CommitIncidentMethod(call, incident.Incident.ID, method, "", plan); err != nil {
			return RoutineMoodReliefResult{}, err
		}
		return RoutineMoodReliefResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
	}
	return RoutineMoodReliefResult{Reason: BuildingMethodUsed}, nil
}
