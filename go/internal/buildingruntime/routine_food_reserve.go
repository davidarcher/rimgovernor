package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func (r *RoutineReviewer) reviewReserve(p *observation.ColonyProjection) {
	p.Facts.FoodReserve = domain.Unknown[policy.FoodReserveReview]()
	supply, known := p.FoodSupply.Value()
	if !known || r.policy.FoodReserveDays == 0 {
		return
	}
	reserve, err := policy.ReviewFoodReserve(supply, nil, r.policy.FoodReserveDays, r.seasonal(p.Facts).FoodMinDays, foodDeliveryDays(p.Facts.FoodPlan))
	if err == nil {
		p.Facts.FoodReserve = domain.Known(reserve)
	}
}

// Unknown sources cannot certify that nothing arrives before exhaustion.
// Support rows and stored ingredients have no delivery rate.
func foodDeliveryDays(f domain.Fact[policy.FoodPlan]) domain.Fact[[]float64] {
	plan, known := f.Value()
	if !known || len(plan.Unknown) > 0 {
		return domain.Unknown[[]float64]()
	}
	days := []float64{}
	for _, row := range plan.Portfolio {
		if row.Decision == policy.FoodPlanClose || row.DeliveredPerDay <= 0 {
			continue
		}
		lead, known := row.Channel.LeadDays.Value()
		if !known {
			return domain.Unknown[[]float64]()
		}
		days = append(days, lead)
	}
	return domain.Known(days)
}

// Reserve access uses the same durable supply actions as ordinary supplies.
// Locations come from a fresh bounded supply census; Hands rechecks them.
func (r *RoutineFoodStorageUpkeepPlanner) admitReserve(ctx, epoch context.Context, goal store.GoalState, observed *o.ColonyFactsSnapshot, reserve policy.FoodReserveReview) (RoutineFoodStorageUpkeepResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	wanted := map[string]bool{}
	for _, id := range reserve.Hold {
		wanted[id] = true
	}
	for _, id := range reserve.Release {
		wanted[id] = false
	}
	method := domain.MethodID(fmt.Sprintf("food-reserve-%d", goal.Revision))
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-reserve-%x", digest[:16]))
	started := r.reviewer.clock.Now()
	native, ok := r.native.(interface {
		ReadFoodReserveSupplies(context.Context, *c.Identity, bool) (bridge.SupplyRead, bridge.Result, error)
	})
	if !ok {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUnknown}, nil
	}
	var actions []domain.Action
	for _, forbid := range []bool{false, true} {
		if forbid && len(reserve.Hold) == 0 || !forbid && len(reserve.Release) == 0 {
			continue
		}
		read, _, err := native.ReadFoodReserveSupplies(ctx, boundary.Identity(state.Snapshot), forbid)
		if err != nil {
			return RoutineFoodStorageUpkeepResult{}, err
		}
		if _, err := boundary.Context(read.Context, state.Snapshot); err != nil || read.Context.GetTick() < observed.Context.GetTick() {
			return RoutineFoodStorageUpkeepResult{}, ErrControl
		}
		for _, target := range read.Targets {
			if hold, selected := wanted[target.Supply.Thing()]; !selected || hold != forbid || target.Supply.Forbidden() != forbid {
				continue
			}
			action, err := domain.NewSupplyAllowAction(domain.ActionID(fmt.Sprintf("%s-%d", id, len(actions))), target.Supply)
			if err != nil {
				return RoutineFoodStorageUpkeepResult{}, err
			}
			actions = append(actions, action)
			if len(actions) == 8 {
				break
			}
		}
		if len(actions) == 8 {
			break
		}
	}
	if len(actions) == 0 {
		return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodUnknown}, nil
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if err = p.current(ctx, epoch); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineFoodStorageUpkeepResult{}, ErrControl
	}
	_, err = p.journal.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, method, plan)
	return RoutineFoodStorageUpkeepResult{Reason: BuildingMethodAdmitted, Plan: id}, err
}
