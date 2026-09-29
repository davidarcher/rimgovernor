package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// mortarsBuilt counts the layout's mortars (#1206) once the tier stood in
// the latest census; a colony without a layout or a built tier has none.
func mortarsBuilt(ctx context.Context, journal *store.Store, snapshot domain.GenerationSnapshot) (int, error) {
	record, ok, err := journal.LoadDefenseLayout(ctx, store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map})
	if err != nil || !ok {
		return 0, err
	}
	tier, _, ok := record.Tier(policy.TierMortars)
	if !ok || !tier.Built {
		return 0, nil
	}
	return len(tier.Buildings), nil
}

// shellTargets is the armory's shell stock for the projection's colony.
func shellTargets(ctx context.Context, journal *store.Store, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection) ([]policy.Amount, error) {
	mortars, err := mortarsBuilt(ctx, journal, snapshot)
	if err != nil {
		return nil, err
	}
	return policy.MortarShellTargets(mortars, policy.AssessArmory(projection.Facts.RaidPoints, projection.Facts.Research)), nil
}

// stockShells admits one stock-target shell bill under MaintainEquipment
// for the first shell no bench bill produces (#1207). Native keeps the
// stock from then on; the planner only adds missing bills.
func (r *RoutineArmoryPlanner) stockShells(call, epoch context.Context, state ControlState, review store.RoutineReview, projection observation.ColonyProjection, holds []policy.Amount) (RoutineArmoryResult, error) {
	p := r.reviewer.player
	targets, err := shellTargets(call, p.journal, state.Snapshot, projection)
	if err != nil || len(targets) == 0 {
		return RoutineArmoryResult{Reason: BuildingMethodNoDeficit}, err
	}
	if short, _ := policy.ShellsShort(targets, projection.Resources).Value(); !short {
		return RoutineArmoryResult{Reason: BuildingMethodNoDeficit}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainEquipment)
	if err != nil || !workable {
		return RoutineArmoryResult{Reason: BuildingMethodNoDeficit}, err
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineArmoryResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineArmoryResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	var stock []policy.Stock
	if names := recipeIngredientNames(census, ""); len(holds) > 0 && len(names) > 0 {
		if stock, _, err = r.native.ReadSupplyStock(call, identity, names); err != nil {
			return RoutineArmoryResult{}, err
		}
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
	}
	choice, ok := policy.SelectShellBill(benches, targets, stock, holds)
	if !ok {
		return RoutineArmoryResult{Reason: BuildingMethodUsed}, nil
	}
	id := domain.MintPlanID()
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, domain.StockTarget, int32(min(choice.Target, 10000)))
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineArmoryResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineArmoryResult{}, fmt.Errorf("%w: stockShells: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	method := domain.MethodID(fmt.Sprintf("armory-shell-%s-%s", choice.Recipe, id))
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineArmoryResult{}, err
	}
	return RoutineArmoryResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
