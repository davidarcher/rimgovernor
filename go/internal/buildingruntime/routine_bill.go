package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

type RoutineBillPlanner struct {
	reviewer *RoutineReviewer
	need     policy.GoalID
	purpose  policy.BillPurpose
	native   BillPlannerNative
}
type RoutineBillResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

type BillPlannerNative interface {
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
}

// NewRoutineBillPlanner composes one bill purpose: cooking serves
// EnsureCooking, preservation and butchery EnsureFoodSupply, and the
// cook-ahead bill MaintainRefrigeration under a solar flare (#408).
func NewRoutineBillPlanner(reviewer *RoutineReviewer, native BillPlannerNative, purpose policy.BillPurpose) (*RoutineBillPlanner, error) {
	if reviewer == nil || native == nil || (purpose != policy.CookFood && purpose != policy.PreserveFood && purpose != policy.ButcherFood && purpose != policy.CookAheadFood) {
		return nil, ErrControl
	}
	need := policy.EnsureFoodSupply
	switch purpose {
	case policy.CookFood:
		need = policy.EnsureCooking
	case policy.CookAheadFood:
		need = policy.MaintainRefrigeration
	}
	return &RoutineBillPlanner{reviewer: reviewer, native: native, purpose: purpose, need: need}, nil
}
func (r *RoutineBillPlanner) Step(ctx context.Context) (RoutineBillResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineBillResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}
func (r *RoutineBillPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBillResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBillResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown {
		return RoutineBillResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBillResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineBillResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == r.need {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			break
		}
	}
	if err != nil {
		return RoutineBillResult{}, err
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineBillResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if goal.Goal.Priority >= 3 {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == r.need && row.Selected
		}
		if !selected {
			return RoutineBillResult{Reason: BuildingMethodRefused}, nil
		}
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineBillResult{}, err
		}
		if r.need == policy.EnsureFoodSupply {
			// Fields, foraging and hunts share the goal and stay open for
			// days; only an open bill is this planner's own work (#260).
			for _, progress := range plan.Progress {
				if progress.Action().Kind() == domain.ProductionBillAction && domain.GoalWorkOpen([]domain.Progress{progress}) {
					return RoutineBillResult{Reason: BuildingMethodExistingWork}, nil
				}
			}
			continue
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineBillResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	plans, err := p.journal.LoadPlans(call, 256)
	if err != nil {
		return RoutineBillResult{}, err
	}
	playerPlans, err := p.journal.PlayerPlans(call, playerWorld(state.Snapshot))
	if err != nil {
		return RoutineBillResult{}, err
	}
	definitions := routineProjectDefinitions(plans, state.Snapshot, playerPlans)
	identity, _, err := r.reviewer.native.Identity(call)
	if err != nil {
		return RoutineBillResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineBillResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineBillResult{}, ErrControl
	}
	claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
	if err != nil {
		return RoutineBillResult{}, err
	}
	read, err := r.reviewer.observeOwned(call, r.reviewer.native, expected, claims, definitions...)
	if err != nil {
		return RoutineBillResult{}, err
	}
	projection := read.Projection
	if r.purpose == policy.ButcherFood {
		// Owed on the food runway alone (#260): native offers no hunt row
		// until a usable bench carries this bill, so waiting for an armed
		// colonist would serialise spot, bill and hunt behind the equip family.
		days, dk := projection.Facts.FoodDays.Value()
		if !dk || days >= r.reviewer.seasonal(projection.Facts).FoodTargetDays {
			return RoutineBillResult{Reason: BuildingMethodUnknown}, nil
		}
		// A butcher bench that shares a cooking room feeds the colony but keeps
		// the kitchen dirty (issue #6 slice 2). While every bench is co-located
		// the separated-spot build owns the goal: the bill waits until that
		// method has been tried (admitted, completed or failed) and then
		// prefers whichever bench stands apart. A forever bill would otherwise
		// hold the goal open until a corpse arrives.
		if rows, known := projection.ProductionBenches.Value(); known && policy.AllButchersColocated(rows) {
			if _, loadErr := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, "butcher-spot-separated"); errors.Is(loadErr, store.ErrNotFound) {
				return RoutineBillResult{Reason: BuildingMethodSeparation}, nil
			} else if loadErr != nil {
				return RoutineBillResult{}, loadErr
			}
		}
	}
	benches, atRisk := projection.ProductionBenches, projection.FoodAtRiskNutrition
	if r.purpose == policy.CookAheadFood {
		// Only under a solar flare with a known remaining duration: the
		// coolers are dark for the outage, so the warm at-risk stock the
		// refrigeration review latched on is cooked instead. A bench whose
		// meal recipe this load already claimed (the ordinary cooking bill)
		// is dropped from the census: one claim per bench and recipe.
		if !policy.SolarFlareHold(projection.Facts.DisasterConditions) {
			return RoutineBillResult{Reason: BuildingMethodNoDeficit}, nil
		}
		refrigeration, err := policy.ReviewRefrigeration(projection.Facts.FoodStorageUpkeep, review.Latches.Refrigeration, r.reviewer.policy.FoodStorage)
		if err != nil {
			return RoutineBillResult{}, err
		}
		atRisk = refrigeration.WarmNutrition
		if benches, err = r.unclaimedBenches(call, state.Snapshot, benches); err != nil {
			return RoutineBillResult{}, err
		}
	}
	var reserve []policy.FoodReserveReview
	if r.purpose == policy.PreserveFood {
		supply, known := projection.FoodSupply.Value()
		if !known {
			return RoutineBillResult{Reason: BuildingMethodUnknown}, nil
		}
		value, err := policy.ReviewFoodReserve(supply, nil, policy.DefaultFoodReserveDays, r.reviewer.seasonal(projection.Facts).FoodMinDays, domain.Unknown[[]float64]())
		if err != nil {
			return RoutineBillResult{Reason: BuildingMethodUnknown}, nil
		}
		reserve = append(reserve, value)
	}
	selected, known := policy.SelectProductionBill(r.purpose, benches, projection.Facts.Colonists, projection.Facts.FoodDays, atRisk, r.reviewer.seasonal(projection.Facts).FoodTargetDays, reserve...)
	if !known {
		return RoutineBillResult{Reason: BuildingMethodUnknown}, nil
	}
	claimed, err := p.journal.BillClaimed(call, state.Snapshot, selected.Bench, selected.Recipe)
	if err != nil {
		return RoutineBillResult{}, err
	}
	if claimed {
		return RoutineBillResult{Reason: BuildingMethodUsed}, nil
	}
	// The bill planners of one step run concurrently and read the same
	// bench token; the second bill on a bench would hold forever on the
	// first's write (#408). One bill per bench per step.
	if arbiter != nil && !arbiter.tryClaim(nil, "bench:"+selected.Bench) {
		return RoutineBillResult{Reason: BuildingMethodUsed}, nil
	}
	hash := sha256.New()
	fmt.Fprintf(hash, "%s/%s/%s/%d", selected.Bench, selected.Recipe, selected.Mode, selected.Target)
	method := domain.MethodID(fmt.Sprintf("bill-%x", hash.Sum(nil)[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBillResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBillResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-bill-%x", digest[:16]))
	value, err := domain.NewProductionBill(selected.Bench, selected.Recipe, selected.Token, selected.Mode, selected.Target)
	if err != nil {
		return RoutineBillResult{}, err
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(string(id)+"-0"), value)
	if err != nil {
		return RoutineBillResult{}, err
	}
	preview, _, err := r.native.PreviewBill(call, boundary.Identity(state.Snapshot), value)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) {
		// A native refusal is a planning outcome for this bench, not a step
		// failure: the sibling planners of the same step keep their turn.
		clockSchedulerLog("%s: bill preview refused bench=%s recipe=%s code=%v detail=%q", goal.Goal.ID, selected.Bench, selected.Recipe, refused.Value.GetCode(), refused.Value.GetDetail())
		return RoutineBillResult{Reason: BuildingMethodRefused}, nil
	}
	if err != nil {
		return RoutineBillResult{}, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return RoutineBillResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(v.Context, state.Snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
		return RoutineBillResult{}, ErrControl
	}
	actions := []domain.Action{action}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineBillResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineBillResult{}, err
	}
	if p.session.State() != state {
		return RoutineBillResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoutineBillResult{}, observation.ErrStale
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBillResult{}, err
	}
	return RoutineBillResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// unclaimedBenches copies the bench census with every recipe this load has
// already claimed, or admitted and not yet written, on its bench marked
// unavailable, so the selection lands
// on a bench and recipe the bill claim table still admits.
func (r *RoutineBillPlanner) unclaimedBenches(ctx context.Context, snapshot domain.GenerationSnapshot, benches domain.Fact[[]policy.ProductionBench]) (domain.Fact[[]policy.ProductionBench], error) {
	rows, known := benches.Value()
	if !known {
		return benches, nil
	}
	out := make([]policy.ProductionBench, 0, len(rows))
	for _, bench := range rows {
		bench.Recipes = append([]policy.ProductionRecipe(nil), bench.Recipes...)
		for i, recipe := range bench.Recipes {
			claimed, err := r.reviewer.player.journal.BillClaimed(ctx, snapshot, bench.ID, recipe.Name)
			if err != nil {
				return benches, err
			}
			if !claimed {
				// An admitted bill not yet written: the sibling planner of
				// this step chose the bench from the same before-token.
				if claimed, err = r.reviewer.player.journal.BillPending(ctx, bench.ID, recipe.Name); err != nil {
					return benches, err
				}
			}
			if claimed {
				bench.Recipes[i].Available = domain.Known(false)
			}
		}
		out = append(out, bench)
	}
	return domain.Known(out), nil
}
