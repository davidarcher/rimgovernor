package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoutineMoodReliefSource reuses the generic bridge.ReadPawns, the same
// call MoodReliefBoundary's dispatch-time inspection uses: its PawnState
// rows carry the Job and Settings/Schedule fields moodReliefDispatchFacts
// decodes into the ExpectedJob/ExpectedScheduleDef commit-time fencing
// values EnsureMood-* relief needs, unlike RoutineWasteSource's narrower
// ReadTendPawns.
type RoutineMoodReliefSource interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

type RoutineMoodReliefPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineMoodReliefSource
}
type RoutineMoodReliefResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineMoodReliefPlanner(reviewer *RoutineReviewer, native RoutineMoodReliefSource) (*RoutineMoodReliefPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineMoodReliefPlanner{reviewer, native}, nil
}
func (r *RoutineMoodReliefPlanner) Step(ctx context.Context) (RoutineMoodReliefResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineMoodReliefResult{}, err
	}
	defer done()
	return r.step(call, epoch)
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
	state := policy.MoodState{Pawn: row, Active: s.Active, Missing: s.Missing, MentalRisk: s.MentalRisk}
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
// goal, this epoch) already reached maxMedicalAttemptsPerPatient: passing
// these to policy.SelectMoodMethod lets it move on to the pawn's next
// measured cause instead of proposing an exhausted one again, mirroring how
// RoutineHusbandryPlanner keys attempts by method to avoid cross-method
// collisions (3a6b30b8).
func moodReliefUsedNeeds(methods []domain.GoalMethod, epoch uint64, pawn policy.PawnID) []policy.MoodNeed {
	var used []policy.MoodNeed
	for _, need := range []policy.MoodNeed{policy.MoodFood, policy.MoodRest, policy.MoodJoy} {
		prefix := fmt.Sprintf("mood-%s-%s-", need, pawn)
		if medicalAttemptCount(methods, epoch, prefix) >= maxMedicalAttemptsPerPatient {
			used = append(used, need)
		}
	}
	return used
}

func (r *RoutineMoodReliefPlanner) step(call, epoch context.Context) (RoutineMoodReliefResult, error) {
	p := r.reviewer.player
	sessionState := p.session.State()
	if !sessionState.Enabled {
		return RoutineMoodReliefResult{Reason: BuildingMethodDisabled}, nil
	}
	if !sessionState.ObservationKnown || sessionState.Snapshot.Validate() != nil || sessionState.Snapshot.Native == 0 {
		return RoutineMoodReliefResult{}, ErrControl
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
	longitude, longitudeKnown := r.reviewer.longitude.Value()
	if !longitudeKnown {
		// The map-local-hour ingredient EnsureMood-* fencing needs is
		// unknown; never guess a fencing value, so no relief can be
		// proposed this Step.
		return RoutineMoodReliefResult{Reason: BuildingMethodUsed}, nil
	}
	statesByGoal := map[domain.GoalID]store.RoutineMoodState{}
	for _, s := range review.Mood.States {
		statesByGoal[policy.MoodGoal(s.Pawn.ID)] = s
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(sessionState.Snapshot)
	for _, binding := range review.Goals {
		if !policy.IsMoodGoal(binding.Need) {
			continue
		}
		goal, err := p.journal.LoadGoal(call, binding.Goal)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
			continue
		}
		open := false
		for _, method := range goal.Methods {
			plan, err := p.journal.LoadPlan(call, method.Plan)
			if err != nil {
				return RoutineMoodReliefResult{}, err
			}
			open = open || domain.GoalWorkOpen(plan.Progress)
		}
		if open {
			return RoutineMoodReliefResult{Reason: BuildingMethodExistingWork}, nil
		}
		moodState, found := statesByGoal[binding.Need]
		if !found {
			continue
		}
		policyState := moodReliefPolicyState(moodState)
		used := moodReliefUsedNeeds(goal.Methods, goal.Goal.Epoch, moodState.Pawn.ID)
		proposal, err := policy.SelectMoodMethod(policyState, used)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		if proposal.Reason != policy.MoodRelief {
			continue
		}
		need, ok := domainMoodReliefNeed(proposal.Need)
		if !ok {
			return RoutineMoodReliefResult{}, ErrControl
		}
		prefix := fmt.Sprintf("mood-%s-%s-", proposal.Need, moodState.Pawn.ID)
		attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
		if attempt >= maxMedicalAttemptsPerPatient {
			continue
		}
		reply, _, err := r.native.ReadPawns(call, identity, []string{string(moodState.Pawn.ID)})
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		observed := reply.GetObserved()
		if observed == nil {
			return RoutineMoodReliefResult{}, ErrControl
		}
		if _, err = boundary.Context(observed.Context, sessionState.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
			return RoutineMoodReliefResult{}, ErrControl
		}
		counts := observed.Completeness
		if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 1 || counts.GetReturned() != 1 || len(observed.Pawns) != 1 {
			return RoutineMoodReliefResult{}, ErrControl
		}
		row := observed.Pawns[0]
		if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(moodState.Pawn.ID) {
			return RoutineMoodReliefResult{}, ErrControl
		}
		job, def, ok := moodReliefDispatchFacts(row, observed.Context.GetTick(), longitude)
		if !ok {
			// Never guess a fencing value; try the next mood goal instead.
			continue
		}
		domainJob, ok := moodReliefJobDomain(job)
		if !ok {
			continue
		}
		relief, err := domain.NewMoodRelief(domain.PawnID(moodState.Pawn.ID), need, domainJob, def)
		if err != nil {
			return RoutineMoodReliefResult{}, err
		}
		method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
		digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
		id := domain.PlanID(fmt.Sprintf("routine-mood-relief-%x", digest[:16]))
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
			return RoutineMoodReliefResult{}, ErrControl
		}
		if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
			return RoutineMoodReliefResult{}, err
		}
		return RoutineMoodReliefResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
	}
	return RoutineMoodReliefResult{Reason: BuildingMethodUsed}, nil
}
