package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoundsHusbandryPlanner proposes one Husbandry training, tame, release or
// slaughter write for MaintainHerd's deficit: policy.AnimalHerdDeficit/
// SelectHusbandryMethod already recognize and pick from the same generic
// animal census MaintainAnimalContainment reads (AnimalState already
// carries training, tame and removal eligibility facts, so no dedicated
// per-cycle husbandry read is needed to detect or select). Taming uses the
// effective operator/food floor. Removal always needs its operator opt-in;
// food slaughter additionally requires an admitted portfolio offer.
type RoundsHusbandryPlanner struct {
	reviewer *Rounder
}
type RoundsHusbandryResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks is lent while training, animal designations or products
	// wait on ordinary native jobs.
	NativeWorkTicks uint32
}

func NewRoundsHusbandryPlanner(reviewer *Rounder) (*RoundsHusbandryPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsHusbandryPlanner: reviewer == nil", ErrControl)
	}
	return &RoundsHusbandryPlanner{reviewer}, nil
}

func (r *RoundsHusbandryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsHusbandryResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsHusbandryResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoundsHusbandryResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsHusbandryResult{Verdict: BuildingReasonNoReview}, nil
	}
	// Read before the goal check: an open animal-product channel lends
	// game time whether or not the herd goal is in deficit.
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoundsHusbandryResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	// The room census: the vet room is ready only once it stands shelled.
	read, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	read.Projection.Facts.VetRoom.Ready = vetRoomReady(read.Projection)
	wait := animalProductWait(read.Projection.Facts.FoodPlan)
	herd := read.Projection.Facts.HerdPolicy()
	nativeWorkPending := policy.HerdWorkPending(read.Projection.Facts.AnimalUpkeep.Animals, read.Projection.Facts.AnimalUpkeep.WildAnimals, herd)
	if nativeWorkPending {
		wait = max(wait, stockWaitTicks)
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainHerd)
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	if !workable {
		return RoundsHusbandryResult{Verdict: BuildingReasonNoDeficit, NativeWorkTicks: wait}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoundsHusbandryResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoundsHusbandryResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: wait}, nil
		}
	}
	upkeep := read.Projection.Facts.AnimalUpkeep
	animals := upkeep.Animals
	// The tame fallback is gated on the animal feed runway, so a herd already
	// short of feed never takes on another mouth.
	feedShort := policy.HerdFeedShort(read.Projection.Facts.AnimalFeedRunway().Projection)
	handlers := domain.Unknown[[]policy.PawnProfile]()
	if pawns, known := read.Projection.WorkPawns.Value(); known {
		handlers = domain.Known(policy.Profiles(pawns))
	}
	// Sheltering a race in danger outdoors comes first: its animals die of
	// the exposure while a training or surplus write waits a cycle.
	choice, err := read.Projection.Facts.AnimalShelterChoice()
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.ReconcileHerdRemoval(animals, herd, read.Projection.Facts.FoodPlan)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.SelectHusbandryMethod(animals, upkeep.WildAnimals, feedShort, herd, handlers)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.FoodSlaughterChoice(read.Projection.Facts.FoodPlan, animals, herd)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.FoodTameChoice(read.Projection.Facts.FoodPlan, upkeep.WildAnimals, feedShort, handlers)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.HerdMasterChoice(animals, herd, handlers)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.SterilizeChoice(animals, herd, read.Projection.Facts.VetRoom)
	}
	switch choice.Reason {
	case policy.HusbandryNoDeficit:
		if nativeWorkPending {
			return RoundsHusbandryResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: wait}, nil
		}
		return RoundsHusbandryResult{Verdict: BuildingReasonNoDeficit, NativeWorkTicks: wait}, nil
	case policy.HusbandryUnknown:
		return RoundsHusbandryResult{Verdict: fieldUnavailable("husbandry_census")}, nil
	}
	// Identity counts all prior methods; the budget counts only native
	// refusals. Each animal's trainable has its own budget.
	prefix := fmt.Sprintf("%s-%s-", choice.Method, choice.Animal)
	if choice.Method == domain.HusbandryTrain {
		prefix = fmt.Sprintf("%s-%s-%s-", choice.Method, choice.Animal, choice.TrainableDef)
	}
	if verdict, ok, err := admitSubject(call, p.journal, prefix, standardMethodPlans(goal.History, goal.Standard.Episode, prefix), state.Snapshot); err != nil {
		return RoundsHusbandryResult{}, err
	} else if !ok {
		return RoundsHusbandryResult{Verdict: verdict, NativeWorkTicks: wait}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Animal)}) {
		return RoundsHusbandryResult{Verdict: claimHeld("animal")}, nil
	}
	method := nextMethodID(prefix, historyMethodIDs(goal.History, goal.Standard.Episode))
	argument := choice.TrainableDef
	if choice.Argument != "" {
		argument = choice.Argument
	}
	husbandry, err := domain.NewHusbandry(domain.PawnID(choice.Animal), choice.Method, argument)
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewHusbandryAction(domain.ActionID(fmt.Sprintf("%s-0", id)), husbandry)
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsHusbandryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoundsHusbandryResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoundsHusbandryResult{}, err
	}
	return RoundsHusbandryResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// animalProductWait is the game time an open animal-product channel needs:
// its nutrition arrives through ordinary native gathering jobs.
func animalProductWait(plan domain.Fact[policy.FoodPlan]) uint32 {
	v, known := plan.Value()
	if !known {
		return 0
	}
	for _, entry := range v.Portfolio {
		if entry.Channel.Kind == policy.CandidateAnimalProduct && entry.Selected() {
			return stockWaitTicks
		}
	}
	return 0
}
