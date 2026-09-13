package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// RoutineResourceSource is the native census RoutineResourcePlanner reads
// immediately before proposing a MaintainResource method. Like
// RoutineMedicalPlanner, it reads a fresh top-level resource stock census
// (not gated behind the planning flag) plus the same generic bench/recipe
// census and ingredient stock funding GearProduce/MaintainMedicalReserves
// already established. PreviewBill re-checks one already-selected
// bench/recipe bill immediately before dispatch, the same
// acceptance-not-authority preview those planners use.
//
// This planner covers only production_policy.py's resource_method
// bench/recipe fallback branch (policy.SelectResourceMethod), the same
// bench-production path GearProduce/MaintainMedicalReserves dispatch
// through. The native mine/harvest source-acquisition branch
// (policy.SelectResourceSources), extraction development and
// material-storage zoning are not dispatched here yet -- see
// docs/BACKLOG.md 05.5.
type RoutineResourceSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
}
type RoutineResourcePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineResourceSource
}
type RoutineResourceResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
}

func NewRoutineResourcePlanner(reviewer *RoutineReviewer, native RoutineResourceSource) (*RoutineResourcePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineResourcePlanner{reviewer, native}, nil
}
func (r *RoutineResourcePlanner) Step(ctx context.Context) (RoutineResourceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

// resourceStockFacts decodes the same generic top-level resource census
// medicalReserveObservationFacts reads (v.Resources), from a freshly read
// ColonyFactsSnapshot rather than the cached routine review snapshot.
func resourceStockFacts(v *o.ColonyFactsSnapshot) domain.Fact[[]policy.Amount] {
	if medicalIssue(v.Issues, "resources") {
		return domain.Unknown[[]policy.Amount]()
	}
	rows := []policy.Amount{}
	for _, q := range v.Resources {
		if q.Units == nil {
			return domain.Unknown[[]policy.Amount]()
		}
		rows = append(rows, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
	}
	return domain.Known(rows)
}

func (r *RoutineResourcePlanner) step(call, epoch context.Context) (RoutineResourceResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineResourceResult{Reason: BuildingMethodDisabled}, nil
	}
	if len(r.reviewer.policy.ResourceTargets) == 0 {
		return RoutineResourceResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineResourceResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoutineResourceResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainResource {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineResourceResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineResourceResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity := boundary.Identity(state.Snapshot)
	reply, _, err := r.native.ReadColonyFacts(call, identity, false, nil)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return RoutineResourceResult{}, ErrControl
	}
	if err = bridge.ValidateColonyFacts(observed, identity); err != nil {
		return RoutineResourceResult{}, ErrControl
	}
	if _, err = boundary.Context(observed.Context, state.Snapshot); err != nil || observed.Context.GetTick() < int64(review.Tick) {
		return RoutineResourceResult{}, ErrControl
	}
	stock := resourceStockFacts(observed)
	resource, target, ok, err := policy.SelectResourceTarget(r.reviewer.policy.ResourceTargets, stock)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if !ok {
		return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
	}
	seen := make([]domain.MethodID, 0, len(goal.Methods))
	for _, method := range goal.Methods {
		seen = append(seen, method.Method)
	}
	census, _, err := r.native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if len(census) > 256 {
		return RoutineResourceResult{}, ErrControl
	}
	benches := make([]policy.GearBench, 0, len(census))
	tokens := map[string]string{}
	ingredients := map[string]bool{}
	for _, row := range census {
		benches = append(benches, row.Bench)
		tokens[row.Bench.ID] = row.Token
		if recipes, known := row.Bench.Recipes.Value(); known {
			for _, recipe := range recipes {
				if slots, known := recipe.Ingredients.Value(); known {
					for _, slot := range slots {
						for _, alt := range slot {
							ingredients[string(alt.Resource)] = true
						}
					}
				}
			}
		}
	}
	names := make([]string, 0, len(ingredients))
	for name := range ingredients {
		names = append(names, name)
	}
	sort.Strings(names)
	var supply []policy.Stock
	if len(names) > 0 {
		supply, _, err = r.native.ReadSupplyStock(call, identity, names)
		if err != nil {
			return RoutineResourceResult{}, err
		}
	}
	choice, err := policy.SelectResourceMethod(policy.ResourceMethodRequest{Resource: resource, Target: target, Seen: seen, Benches: domain.Known(benches), Stock: supply})
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if choice.Kind != policy.ResourceMethodProduce {
		return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
	}
	token, ok := tokens[choice.Bench]
	if !ok {
		return RoutineResourceResult{}, ErrControl
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, choice.ID)))
	id := domain.PlanID(fmt.Sprintf("routine-resource-%x", digest[:16]))
	targetCount := int32(choice.Target)
	if int64(targetCount) != choice.Target {
		return RoutineResourceResult{}, ErrControl
	}
	bill, err := domain.NewProductionBill(choice.Bench, choice.Recipe, token, domain.StockTarget, targetCount)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	preview, _, err := r.native.PreviewBill(call, boundary.Identity(state.Snapshot), bill)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		return RoutineResourceResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineResourceResult{}, ErrControl
	}
	action, err := domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineResourceResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineResourceResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineResourceResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, choice.ID, plan); err != nil {
		return RoutineResourceResult{}, err
	}
	return RoutineResourceResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}
