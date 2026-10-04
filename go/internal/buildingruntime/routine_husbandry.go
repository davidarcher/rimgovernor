package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineHusbandryPlanner proposes one Husbandry training, tame, release or
// slaughter write for MaintainHerd's deficit: policy.AnimalHerdDeficit/
// SelectHusbandryMethod already recognize and pick from the same generic
// animal census MaintainAnimalContainment reads (AnimalState already
// carries training, tame and removal eligibility facts, so no dedicated
// per-cycle husbandry read is needed to detect or select). Taming uses the
// effective operator/food floor. Removal always needs its operator opt-in;
// food slaughter additionally requires an admitted portfolio offer.
type RoutineHusbandryPlanner struct {
	reviewer *Rounder
}
type RoutineHusbandryResult struct {
	Verdict
	Plan domain.PlanID
	// NativeWorkTicks is lent while an open animal-product channel delivers
	// on native jobs alone (milking, egg gathering): the herd needs game
	// time, not a method, and a hold without it parks the clock on no_work.
	NativeWorkTicks uint32
}

func NewRoutineHusbandryPlanner(reviewer *Rounder) (*RoutineHusbandryPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoutineHusbandryPlanner: reviewer == nil", ErrControl)
	}
	return &RoutineHusbandryPlanner{reviewer}, nil
}

func (r *RoutineHusbandryPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineHusbandryResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineHusbandryResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineHusbandryResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineHusbandryResult{Verdict: BuildingReasonNoReview}, nil
	}
	// Read before the goal check: an open animal-product channel lends
	// game time whether or not the herd goal is in deficit.
	expected, err := stepScope(call, r.reviewer.native)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineHusbandryResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	started := r.reviewer.clock.Now()
	// The room census: the vet room is ready only once it stands shelled.
	read, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	read.Projection.Facts.VetRoom.Ready = vetRoomReady(read.Projection)
	wait := animalProductWait(read.Projection.Facts.FoodPlan)
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainHerd)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if !workable {
		return RoutineHusbandryResult{Verdict: BuildingReasonNoDeficit, NativeWorkTicks: wait}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineHusbandryResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineHusbandryResult{Verdict: BuildingReasonExistingWork, NativeWorkTicks: wait}, nil
		}
	}
	upkeep := read.Projection.Facts.AnimalUpkeep
	animals := upkeep.Animals
	// The tame fallback is gated on the same feed review
	// RoutineAnimalFeedPlanner plans from, so a herd already short of feed
	// never takes on another mouth.
	reviewed, err := policy.ReviewAnimalUpkeep(upkeep, review.Latches.Animals, r.reviewer.policy.FoodReserveDays)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	handlers := domain.Unknown[[]policy.PawnProfile]()
	if pawns, known := read.Projection.WorkPawns.Value(); known {
		handlers = domain.Known(policy.Profiles(pawns))
	}
	herd := read.Projection.Facts.HerdPolicy()
	// Sheltering a race in danger outdoors comes first: its animals die of
	// the exposure while a training or surplus write waits a cycle.
	choice, err := read.Projection.Facts.AnimalShelterChoice()
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.ReconcileHerdRemoval(animals, herd, read.Projection.Facts.FoodPlan)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.SelectHusbandryMethod(animals, upkeep.WildAnimals, policy.HerdFeedShort(reviewed), herd, handlers)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.FoodSlaughterChoice(read.Projection.Facts.FoodPlan, animals, herd)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.PrioritizeSlaughterChoice(animals, handlers)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.HerdMasterChoice(animals, herd, handlers)
	}
	if choice.Reason == policy.HusbandryNoDeficit {
		choice = policy.SterilizeChoice(animals, herd, read.Projection.Facts.VetRoom)
	}
	switch choice.Reason {
	case policy.HusbandryNoDeficit:
		return RoutineHusbandryResult{Verdict: BuildingReasonNoDeficit, NativeWorkTicks: wait}, nil
	case policy.HusbandryUnknown:
		return RoutineHusbandryResult{Verdict: fieldUnavailable("husbandry_census")}, nil
	}
	// Keyed by animal, method and attempt count, not trainable: a fresh
	// attempt after an interrupted or failed try re-selects whichever
	// trainable is currently best, mirroring RoutineEquipPlanner's method
	// key. The method name is included so a training attempt count never
	// collides with, or is exhausted by, a slaughter attempt on the same
	// animal (or vice versa) -- they are independent write kinds.
	prefix := fmt.Sprintf("%s-%s-", choice.Method, choice.Animal)
	attempt := medicalAttemptCount(goal.History, goal.Standard.Episode, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineHusbandryResult{Verdict: refuse(RefusalRetriesSpent, "maxMedicalAttemptsPerPatient", "")}, nil
	}
	if !arbiter.tryClaim([]domain.PawnID{domain.PawnID(choice.Animal)}) {
		return RoutineHusbandryResult{Verdict: claimHeld("animal")}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	argument := choice.TrainableDef
	if choice.Method == domain.HusbandryPrioritizeSlaughter {
		argument = string(choice.Handler)
	}
	if choice.Argument != "" {
		argument = choice.Argument
	}
	husbandry, err := domain.NewHusbandry(domain.PawnID(choice.Animal), choice.Method, argument)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	id := domain.MintPlanID()
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
		return RoutineHusbandryResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Standard.ID, goal.Revision, method, plan); err != nil {
		return RoutineHusbandryResult{}, err
	}
	return RoutineHusbandryResult{Verdict: BuildingReasonAdmitted, Plan: id}, nil
}

// animalProductWait is the game time an open animal-product channel needs:
// its nutrition arrives through ordinary native gathering jobs.
func animalProductWait(plan domain.Fact[policy.FoodPlan]) uint32 {
	v, known := plan.Value()
	if !known {
		return 0
	}
	for _, entry := range v.Portfolio {
		if entry.Channel.Kind == policy.FoodAnimalProduct && entry.DeliveredPerDay > 0 {
			return stockWaitTicks
		}
	}
	return 0
}
