package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func (r *RoutineFoodStorageUpkeepPlanner) admitCorpseLarder(ctx, epoch context.Context, arbiter *stepArbiter, goal store.GoalState, observed *o.ColonyFactsSnapshot, choice policy.CorpseLarderMethod) (RoutineFoodStorageUpkeepResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	started := r.reviewer.clock.Now()
	method := domain.MethodID(fmt.Sprintf("corpse-larder-%s-%s-%d", choice.Kind, choice.Stock.ID, goal.Revision))
	id := domain.MintPlanID()
	actionID := domain.ActionID(fmt.Sprintf("%s-0", id))
	var action domain.Action
	var err error
	switch choice.Kind {
	case "allow", "forbid":
		var supply domain.SupplyAllow
		if choice.Kind == "allow" {
			supply, err = domain.NewSupplyAllow(choice.Stock.ID, string(choice.Stock.DefName), choice.Handling.Cell)
		} else {
			supply, err = domain.NewSupplyForbid(choice.Stock.ID, string(choice.Stock.DefName), choice.Handling.Cell)
		}
		if err == nil {
			action, err = domain.NewSupplyAllowAction(actionID, supply)
		}
	case "haul":
		if !arbiter.tryClaim([]domain.PawnID{choice.Handling.Hauler}, "haul-item:"+choice.Stock.ID) {
			return RoutineFoodStorageUpkeepResult{Verdict: waitFor(WaitMethodUsed, "haul_item_claim")}, nil
		}
		var haul domain.Haul
		haul, err = domain.NewHaul(choice.Handling.Hauler, choice.Stock.ID, string(choice.Stock.DefName), choice.Handling.Cell)
		if err == nil {
			action, err = domain.NewHaulAction(actionID, haul)
		}
	default:
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: admitCorpseLarder: kind %q", ErrControl, choice.Kind)
	}
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	if err = p.current(ctx, epoch); err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineFoodStorageUpkeepResult{}, fmt.Errorf("%w: admitCorpseLarder: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	_, err = p.journal.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, method, plan)
	return RoutineFoodStorageUpkeepResult{Verdict: BuildingReasonAdmitted, Plan: id}, err
}
