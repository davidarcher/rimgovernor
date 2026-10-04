package buildingruntime

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutinePopulationJoinerPlanner proposes one QuestAccept write for
// MaintainPopulation's joiner deficit: policy.JoinerDeficit and
// SelectJoinerMethod read the per-cycle visible quest census
// (RoutineFacts.QuestOffers, from the world-progression read the
// RoutineSource offers as RoutineQuestSource) against the bot's own
// population target (domain.PopulationTarget) and the population, sleeping
// and food facts the review already carries. Empire quests and ground
// Odyssey quests (policy.SelectOdysseyQuestMethod) are accepted through the
// same write. Pending WandererJoins letters use the same capacity gate
// and the dialog-answer executor. Offers the colony cannot host expire.
type RoutinePopulationJoinerPlanner struct {
	reviewer *RoutineReviewer
}
type RoutinePopulationJoinerResult struct {
	Verdict
	Plan domain.PlanID
}

func NewRoutinePopulationJoinerPlanner(reviewer *RoutineReviewer) (*RoutinePopulationJoinerPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutinePopulationJoinerPlanner: reviewer == nil", ErrControl)
	}
	return &RoutinePopulationJoinerPlanner{reviewer}, nil
}

func (r *RoutinePopulationJoinerPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutinePopulationJoinerResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutinePopulationJoinerResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutinePopulationJoinerResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPopulation)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if !workable {
		return RoutinePopulationJoinerResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutinePopulationJoinerResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutinePopulationJoinerResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	facts := read.Projection.Facts
	if ceremony, ok := policy.CeremonyStartOf(facts.Royalty); ok {
		return r.admitCeremonyStart(call, epoch, state, goal, ceremony, started)
	}
	if letter, ok := policy.SelectJoinerLetter(facts.JoinerLetters, policy.JoinerCapacity(facts.JoinerCapacity())); ok {
		return r.admitLetter(call, epoch, state, goal, letter, started)
	}
	choice := policy.SelectJoinerMethod(facts.QuestOffers, policy.JoinerCapacity(facts.JoinerCapacity()))
	prefix := "joiner"
	if choice.Reason == policy.JoinerNoOffer || choice.Reason == policy.JoinerNoCapacity {
		if empire := policy.SelectEmpireQuestMethod(facts.QuestOffers, facts.TitleClaimQuests...); empire.Reason == "" {
			choice, prefix = empire, "empire"
		} else if odyssey := policy.SelectOdysseyQuestMethod(facts.QuestOffers); odyssey.Reason == "" {
			choice, prefix = odyssey, "odyssey"
		}
	}
	switch choice.Reason {
	case policy.JoinerNoOffer, policy.JoinerNoCapacity:
		return RoutinePopulationJoinerResult{Verdict: waitFor(WaitMethodUsed, "joiner_offers")}, nil
	case policy.JoinerCensusUnknown:
		return RoutinePopulationJoinerResult{Verdict: fieldUnavailable("joiner_census")}, nil
	}
	// Keyed by quest and attempt count, mirroring
	// RoutinePrisonerInteractionPlanner's method key: a fresh attempt after
	// an interrupted or failed try re-selects whichever offer is current.
	prefix = fmt.Sprintf("%s-%s-", prefix, choice.Quest)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	accept, err := domain.NewQuestAccept(choice.Quest, "", choice.RewardChoice)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewQuestAcceptAction(domain.ActionID(fmt.Sprintf("%s-0", id)), accept)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	return RoutinePopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// admitCeremonyStart commands the bestowing ritual of a ceremony whose
// bestower waits (policy.CeremonyStart, #1639) through the generic Ritual
// write; native refuses while the game offers no start command.
func (r *RoutinePopulationJoinerPlanner) admitCeremonyStart(call, epoch context.Context, state ControlState, goal store.GoalState, ceremony policy.BestowingCeremony, started time.Time) (RoutinePopulationJoinerResult, error) {
	p := r.reviewer.player
	prefix := fmt.Sprintf("ritual-start-%s-", ceremony.Pawn)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	ritual, err := domain.NewRitual(domain.PawnID(ceremony.Pawn), domain.RitualBestowing, domain.RitualStart)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewRitualAction(domain.ActionID(fmt.Sprintf("%s-0", id)), ritual)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: admitCeremonyStart: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	return RoutinePopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

func (r *RoutinePopulationJoinerPlanner) admitLetter(call, epoch context.Context, state ControlState, goal store.GoalState, letter policy.JoinerLetterOffer, started time.Time) (RoutinePopulationJoinerResult, error) {
	p := r.reviewer.player
	prefix := fmt.Sprintf("joiner-letter-%d-", letter.ID)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutinePopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	value, err := domain.NewJoinerLetterAnswer(letter.ID, letter.Label, letter.Token)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	action, err := domain.NewDialogAnswerAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutinePopulationJoinerResult{}, fmt.Errorf("%w: admitLetter: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	return RoutinePopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
