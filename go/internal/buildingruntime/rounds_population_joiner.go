package buildingruntime

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsPopulationJoinerPlanner proposes one QuestAccept write for
// MaintainPopulation's joiner deficit: policy.JoinerDeficit and
// SelectJoinerMethod read the per-cycle visible quest census
// (RoundsFacts.QuestOffers, from the world-progression read the
// RoundsSource offers as RoundsQuestSource) against the bot's own
// population target (domain.PopulationTarget) and the population, sleeping
// and food facts the review already carries. Empire quests and ground
// Odyssey quests (policy.SelectQuestMethod) are accepted through the
// same write. Pending WandererJoins letters use the same capacity gate
// and the dialog-answer executor. Offers the colony cannot host expire.
type RoundsPopulationJoinerPlanner struct {
	reviewer *Rounder
}
type RoundsPopulationJoinerResult struct {
	Verdict
	Plan            domain.PlanID
	NativeWorkTicks uint32
}

func NewRoundsPopulationJoinerPlanner(reviewer *Rounder) (*RoundsPopulationJoinerPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsPopulationJoinerPlanner: reviewer == nil", ErrControl)
	}
	return &RoundsPopulationJoinerPlanner{reviewer}, nil
}

func (r *RoundsPopulationJoinerPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsPopulationJoinerResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsPopulationJoinerResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainPopulation)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	if !workable {
		return RoundsPopulationJoinerResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsPopulationJoinerResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsPopulationJoinerResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsPopulationJoinerResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	facts := read.Projection.Facts
	if ceremony, ok := policy.CeremonyStartOf(facts.Royalty); ok {
		return r.admitCeremonyStart(call, epoch, state, goal, ceremony, started)
	}
	if letter, ok := policy.SelectJoinerLetter(facts.JoinerLetters, policy.JoinerCapacity(facts.JoinerCapacity())); ok {
		return r.admitLetter(call, epoch, state, goal, letter, started)
	}
	inspection, inspecting, err := r.admitGravInspection(call, epoch, state, goal, read, arbiter, started)
	if inspecting || err != nil {
		return inspection, err
	}
	refugee, refugeeHandled, err := r.admitRefugeeTend(call, epoch, state, goal, review, read, arbiter, started)
	if refugeeHandled || err != nil {
		return refugee, err
	}
	if result, handled, err := r.admitMonument(call, epoch, state, goal, review, read, arbiter, started); handled || err != nil {
		return result, err
	}
	if result, handled, err := r.admitDecree(call, epoch, state, goal, review, read, arbiter, started); handled || err != nil {
		return result, err
	}
	departure, departed, err := r.admitDeparture(call, epoch, state, goal, read, arbiter, started)
	if departed || err != nil {
		return departure, err
	}
	expedition, expeditionHandled, err := r.admitExpedition(call, epoch, state, goal, read, arbiter, started)
	if expeditionHandled || err != nil {
		return expedition, err
	}
	hosting, hosted, err := r.admitHospitality(call, epoch, state, goal, review, read, arbiter, started)
	if hosted || err != nil {
		return hosting, err
	}
	choice := policy.SelectJoinerMethod(facts.QuestOffers, policy.JoinerCapacity(facts.JoinerCapacity()), facts)
	prefix := "joiner"
	if choice.Reason == policy.JoinerNoOffer || choice.Reason == policy.JoinerNoCapacity {
		choice, prefix = policy.SelectQuestMethod(facts), "quest"
	}
	switch choice.Reason {
	case policy.JoinerNoOffer, policy.JoinerNoCapacity, policy.QuestNoOffer:
		if expedition.NativeWorkTicks > 0 {
			return expedition, nil
		}
		if inspection.NativeWorkTicks > 0 {
			return inspection, nil
		}
		if departure.NativeWorkTicks > 0 {
			return departure, nil
		}
		if refugee.NativeWorkTicks > 0 {
			return refugee, nil
		}
		if hosting.NativeWorkTicks > 0 {
			return hosting, nil
		}
		return RoundsPopulationJoinerResult{Verdict: waitFor(WaitMethodUsed, "joiner_offers")}, nil
	case policy.JoinerCensusUnknown:
		return RoundsPopulationJoinerResult{Verdict: fieldUnavailable("joiner_census")}, nil
	}
	// Keyed by quest and attempt count, mirroring
	// RoundsPrisonerInteractionPlanner's method key: a fresh attempt after
	// an interrupted or failed try re-selects whichever offer is current.
	prefix = fmt.Sprintf("%s-%s-", prefix, choice.Quest)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	accept, err := domain.NewQuestAccept(choice.Quest, choice.Accepter, choice.RewardChoice)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewQuestAcceptAction(domain.ActionID(fmt.Sprintf("%s-0", id)), accept)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPopulationJoinerResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// admitCeremonyStart commands the bestowing ritual of a ceremony whose
// bestower waits (policy.CeremonyStart, #1639) through the generic Ritual
// write; native refuses while the game offers no start command.
func (r *RoundsPopulationJoinerPlanner) admitCeremonyStart(call, epoch context.Context, state ControlState, goal store.StandardState, ceremony policy.BestowingCeremony, started time.Time) (RoundsPopulationJoinerResult, error) {
	p := r.reviewer.player
	prefix := fmt.Sprintf("ritual-start-%s-", ceremony.Pawn)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	ritual, err := domain.NewRitual(domain.PawnID(ceremony.Pawn), domain.RitualBestowing, domain.RitualStart)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewRitualAction(domain.ActionID(fmt.Sprintf("%s-0", id)), ritual)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPopulationJoinerResult{}, fmt.Errorf("%w: admitCeremonyStart: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

func (r *RoundsPopulationJoinerPlanner) admitLetter(call, epoch context.Context, state ControlState, goal store.StandardState, letter policy.JoinerLetterOffer, started time.Time) (RoundsPopulationJoinerResult, error) {
	p := r.reviewer.player
	prefix := fmt.Sprintf("joiner-letter-%d-", letter.ID)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoundsPopulationJoinerResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	value, err := domain.NewJoinerLetterAnswer(letter.ID, letter.Label, letter.Token)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	action, err := domain.NewDialogAnswerAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsPopulationJoinerResult{}, fmt.Errorf("%w: admitLetter: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	return RoundsPopulationJoinerResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}
