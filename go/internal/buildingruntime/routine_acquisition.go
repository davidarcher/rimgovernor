package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RoutineAcquisitionPlanner struct {
	reviewer *RoutineReviewer
	need     policy.GoalID
}
type RoutineAcquisitionResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineAcquisitionPlanner(reviewer *RoutineReviewer, need policy.GoalID) (*RoutineAcquisitionPlanner, error) {
	if reviewer == nil || (need != policy.MaintainWood && need != policy.EnsureFoodSupply) {
		return nil, ErrControl
	}
	return &RoutineAcquisitionPlanner{reviewer, need}, nil
}
func (r *RoutineAcquisitionPlanner) Step(ctx context.Context) (RoutineAcquisitionResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}
func (r *RoutineAcquisitionPlanner) step(call, epoch context.Context) (RoutineAcquisitionResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineAcquisitionResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineAcquisitionResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineAcquisitionResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == r.need {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			break
		}
	}
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineAcquisitionResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == r.need && row.Selected
		}
		if !selected {
			return RoutineAcquisitionResult{Reason: BuildingMethodRefused}, nil
		}
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineAcquisitionResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot)
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineAcquisitionResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	read, err := observation.ObserveRoutineOwned(call, r.reviewer.native, r.reviewer.clock, expected, r.reviewer.maxAge, claims, definitions...)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	projection := read.Projection
	pending := projection.PendingWoodUnits
	deficit := domain.Unknown[float64]()
	food := r.need == policy.EnsureFoodSupply
	if food {
		pending = projection.PendingFoodNutrition
		supply, known := projection.CombinedFoodSupply.Value()
		humans, hk := projection.FoodSupply.Value()
		if known && hk {
			ids := []policy.PawnID{}
			for _, human := range humans.Consumers {
				ids = append(ids, human.ID)
			}
			forecast, err := policy.ForecastFood(supply, ids)
			if err != nil {
				return RoutineAcquisitionResult{}, err
			}
			need := 0.0
			for _, consumer := range forecast.Consumers {
				need += max(0, r.reviewer.policy.FoodTargetDays*consumer.NutritionPerDay-consumer.UsableNutrition)
			}
			deficit = domain.Known(need)
		}
	} else if wood, known := projection.Facts.Wood.Value(); known {
		deficit = domain.Known(max(0, float64(r.reviewer.policy.WoodTarget)-float64(wood)))
	}
	held := map[string]bool{}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			if acquisition, ok := progress.Action().Acquisition(); ok && domain.GoalWorkOpen([]domain.Progress{progress}) {
				held[acquisition.Thing()] = true
			}
		}
	}
	slots := domain.Unknown[int]()
	if n, known := projection.PendingHunts.Value(); known {
		slots = domain.Known(max(0, 2-n))
	}
	selected, err := policy.SelectAcquisition(projection.Acquisition, deficit, pending, food, held, slots)
	if err != nil {
		return RoutineAcquisitionResult{Reason: BuildingMethodUnknown}, nil
	}
	if len(selected) == 0 {
		return RoutineAcquisitionResult{Reason: BuildingMethodUsed}, nil
	}
	hash := sha256.New()
	for _, row := range selected {
		fmt.Fprintf(hash, "%s/%s/%s/%d/%d\n", row.ID, row.Resource, row.Token, row.Cell.X, row.Cell.Z)
	}
	method := domain.MethodID(fmt.Sprintf("acquire-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineAcquisitionResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineAcquisitionResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-acquire-%x", digest[:16]))
	var actions []domain.Action
	for i, row := range selected {
		value, err := domain.NewAcquisition(row.ID, row.Resource, row.Cell)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		action, err := domain.NewAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
		if err != nil {
			return RoutineAcquisitionResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineAcquisitionResult{}, err
	}
	if p.session.State() != state {
		return RoutineAcquisitionResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineAcquisitionResult{}, err
	}
	return RoutineAcquisitionResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
