package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineHusbandryPlanner proposes one Husbandry training, tame, release or
// slaughter write for MaintainHerd's deficit: policy.AnimalHerdDeficit/
// SelectHusbandryMethod already recognize and pick from the same generic
// animal census MaintainAnimalContainment reads (AnimalState already
// carries training, tame and removal eligibility facts, so no dedicated
// per-cycle husbandry read is needed to detect or select). Tame and removal
// are only ever proposed once the operator-declared RoutinePolicy herd
// opt-ins are set — see policy.MaintainHerd's doc comment for the disclosed
// narrowing this still carries (no protected-id/breeding-reserve richness).
type RoutineHusbandryPlanner struct {
	reviewer *RoutineReviewer
}
type RoutineHusbandryResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineHusbandryPlanner(reviewer *RoutineReviewer) (*RoutineHusbandryPlanner, error) {
	if reviewer == nil {
		return nil, ErrControl
	}
	return &RoutineHusbandryPlanner{reviewer}, nil
}
func (r *RoutineHusbandryPlanner) Step(ctx context.Context) (RoutineHusbandryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineHusbandryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineHusbandryResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineHusbandryResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineHusbandryResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineHusbandryResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainHerd {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineHusbandryResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineHusbandryResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineHusbandryResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineHusbandryResult{}, ErrControl
	}
	started := r.reviewer.clock.Now()
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	upkeep := read.Projection.Facts.AnimalUpkeep
	animals := upkeep.Animals
	// The tame fallback is gated on the same feed review
	// RoutineAnimalFeedPlanner plans from, so a herd already short of feed
	// never takes on another mouth.
	reviewed, err := policy.ReviewAnimalUpkeep(upkeep, review.Latches.Animals, r.reviewer.policy.AnimalUpkeep)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	handlers := domain.Unknown[[]policy.PawnProfile]()
	if pawns, known := read.Projection.WorkPawns.Value(); known {
		handlers = domain.Known(policy.Profiles(pawns))
	}
	choice := policy.SelectHusbandryMethod(animals, upkeep.WildAnimals, policy.HerdFeedShort(reviewed), r.reviewer.policy.Herd(), handlers)
	switch choice.Reason {
	case policy.HusbandryNoDeficit:
		return RoutineHusbandryResult{Reason: BuildingMethodUsed}, nil
	case policy.HusbandryUnknown:
		return RoutineHusbandryResult{Reason: BuildingMethodUnknown}, nil
	}
	// Keyed by animal, method and attempt count, not trainable: a fresh
	// attempt after an interrupted or failed try re-selects whichever
	// trainable is currently best, mirroring RoutineEquipPlanner's method
	// key. The method name is included so a training attempt count never
	// collides with, or is exhausted by, a slaughter attempt on the same
	// animal (or vice versa) -- they are independent write kinds.
	prefix := fmt.Sprintf("%s-%s-", choice.Method, choice.Animal)
	attempt := medicalAttemptCount(goal.Methods, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineHusbandryResult{Reason: BuildingMethodExhausted}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Animal)}) {
		return RoutineHusbandryResult{Reason: BuildingMethodUsed}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	husbandry, err := domain.NewHusbandry(domain.PawnID(choice.Animal), choice.Method, choice.TrainableDef)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-husbandry-%x", digest[:16]))
	action, err := domain.NewHusbandryAction(domain.ActionID(fmt.Sprintf("%s-0", id)), husbandry)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineHusbandryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineHusbandryResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineHusbandryResult{}, err
	}
	return RoutineHusbandryResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
