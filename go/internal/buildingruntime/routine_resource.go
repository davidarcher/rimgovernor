package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
	"time"

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
// This planner covers production_policy.py's resource_method bench/recipe
// fallback branch (policy.SelectResourceMethod), the same bench-production
// path GearProduce/MaintainMedicalReserves dispatch through, plus its native
// mine-source acquisition branch (policy.SelectResourceSources): whenever the
// bench/recipe path cannot fund the deficit and a selected source is a mine
// (the only method SelectResourceSources populates Cell/Token for), this
// planner dispatches a domain.MineAcquisitionAction against it through the
// second, independently-registered mine-acquisition vertical (see
// docs/BACKLOG.md 05.5). Extraction development and material-storage zoning
// are still not dispatched here.
type RoutineResourceSource interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, bridge.Result, error)
	PreviewAcquisition(context.Context, *c.Identity, bridge.AcquisitionTarget) (*op.PreviewReply, bridge.Result, error)
}
type RoutineResourcePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineResourceSource
}
type RoutineResourceResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// Sources is populated whenever the bench/recipe production path
	// (policy.SelectResourceMethod) could not fund the dynamically-selected
	// resource and a fresh native ListResourceSources/ReadResourceSources
	// read plus policy.SelectResourceSources found undesignated mine/harvest
	// sources that could cover the outstanding deficit. When the selection
	// includes a mine source (the only method carrying a Cell/Token), it is
	// actually dispatched -- see Reason/Plan -- against the second,
	// independently-registered mine-acquisition vertical; any other selected
	// method is still surfaced here for observability only, since only mine
	// sources carry the CAS evidence this vertical's AcquireResource dispatch
	// needs. A native read failure here is swallowed rather than propagated,
	// since the bench/recipe outcome above already stands on its own.
	Sources []policy.ResourceSource
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
		sources := r.sourcesForDeficit(call, identity, resource, target, stock)
		result, dispatched, err := r.dispatchMineSource(call, epoch, state, goal, resource, sources, started)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if dispatched {
			result.Sources = sources
			return result, nil
		}
		return RoutineResourceResult{Reason: BuildingMethodUsed, Sources: sources}, nil
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

// sourcesForDeficit reads the resource's fresh native mine/harvest sources
// and applies policy.SelectResourceSources against the outstanding deficit,
// mirroring the first half of production_policy.py's resource_method (its
// bill-listing fallback, which SelectResourceMethod above already covers, is
// only reached once this source loop finds nothing to select). A native read
// failure here is deliberately swallowed rather than surfaced, preserving
// the bench/recipe outcome the caller already computed -- dispatchMineSource
// below is what actually acts on the result.

func (r *RoutineResourcePlanner) sourcesForDeficit(ctx context.Context, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount]) []policy.ResourceSource {
	rows, known := stock.Value()
	if !known {
		return nil
	}
	var have int64
	for _, row := range rows {
		if row.Resource == resource {
			have = row.Count
			break
		}
	}
	sources, _, err := r.native.ReadResourceSources(ctx, identity, string(resource))
	if err != nil {
		return nil
	}
	return policy.SelectResourceSources(sources, target, have, 0)
}

// dispatchMineSource actually dispatches a domain.MineAcquisitionAction
// against the selection's mine source, if any -- the second,
// independently-registered mine-acquisition vertical this planner's mine
// dispatch needs, since a mined resource can never appear in the generic
// vertical's AcquisitionFacts census (see docs/BACKLOG.md 05.5). Only a mine
// method source carries the Cell/Token bridge.ReadMineAcquisition/
// AcquireResource need (policy.SelectResourceSources populates them for
// "mine" rows only); any other selected method is left to the caller's
// observability-only Sources reporting. dispatched is false, with a zero
// result and nil error, when there is nothing to dispatch -- the caller then
// falls back to its own BuildingMethodUsed reporting.
func (r *RoutineResourcePlanner) dispatchMineSource(call, epoch context.Context, state ControlState, goal store.GoalState, resource policy.Resource, sources []policy.ResourceSource, started time.Time) (RoutineResourceResult, bool, error) {
	var source policy.ResourceSource
	found := false
	for _, candidate := range sources {
		if candidate.Method == policy.ResourceSourceMine {
			source, found = candidate, true
			break
		}
	}
	if !found {
		return RoutineResourceResult{}, false, nil
	}
	p := r.reviewer.player
	acquisitionValue, err := domain.NewAcquisition(source.ThingID, string(resource), source.Cell)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/mine/%s/%d/%d", goal.Goal.ID, goal.Goal.Epoch, source.ThingID, source.Cell.X, source.Cell.Z)))
	id := domain.PlanID(fmt.Sprintf("routine-resource-mine-%x", digest[:16]))
	methodID := domain.MethodID(fmt.Sprintf("resource-mine-%x", digest[:16]))
	target := bridge.AcquisitionTarget{Acquisition: acquisitionValue, Token: source.Token}
	preview, _, err := r.native.PreviewAcquisition(call, boundary.Identity(state.Snapshot), target)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		return RoutineResourceResult{Reason: BuildingMethodRefused}, true, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineResourceResult{}, false, ErrControl
	}
	action, err := domain.NewMineAcquisitionAction(domain.ActionID(fmt.Sprintf("%s-0", id)), acquisitionValue)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineResourceResult{}, false, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineResourceResult{}, false, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, methodID, plan); err != nil {
		return RoutineResourceResult{}, false, err
	}
	return RoutineResourceResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
}
