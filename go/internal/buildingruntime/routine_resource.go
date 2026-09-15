package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
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
// mine-source acquisition branch (policy.SelectResourceSources) and its
// material-storage zoning branch (policy.SelectResourceStorageZone). Whenever
// the bench/recipe path cannot fund the deficit and a fresh source selection
// includes a "mine" source, that source's covered storage is checked first
// (materialStorageZoneFallback): if a new stockpile zone is needed, it is
// admitted and mine acquisition is deferred to a later tick (mirroring
// production_policy.py's resource_method, which returns its storage
// zone-build action instead of an acquisition action whenever storage is
// inadequate). Only once storage already covers the deficit (or no mine
// source was selected) does this planner dispatch a
// domain.MineAcquisitionAction against the mine source through the second,
// independently-registered mine-acquisition vertical. Extraction development
// is still not dispatched here.
type RoutineResourceSource interface {
	observation.ColonySource
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error)
	PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error)
	ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error)
	PreviewAcquisition(context.Context, *c.Identity, bridge.AcquisitionTarget) (*op.PreviewReply, bridge.Result, error)
	PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
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
	// includes a "mine" source (the only method carrying a Cell/Token), this
	// same read's StorageCapacity payload is checked first
	// (materialStorageZoneFallback): if hauling that source's yield needs a
	// new covered stockpile zone, that zone is admitted (Reason/Plan set
	// exactly like the bench/recipe path) and the mine source itself is not
	// yet dispatched. Once storage already covers the deficit, the mine
	// source is actually dispatched (dispatchMineSource) -- see Reason/Plan
	// -- against the second, independently-registered mine-acquisition
	// vertical; any other selected method is still surfaced here for
	// observability only, since only mine sources carry the CAS evidence
	// this vertical's AcquireResource dispatch needs. A native read failure
	// here is swallowed rather than propagated, since the bench/recipe
	// outcome above already stands on its own.
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
	return r.step(call, epoch, newStepArbiter())
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

func (r *RoutineResourcePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineResourceResult, error) {
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
	return r.dispatchResourceGoal(call, epoch, state, goal, review.Tick, identity, resource, target, stock, started)
}

// dispatchResourceGoal is the shared MaintainResource/MaintainAnimalFeed
// acquisition tail, once each goal's own selection has picked one
// (resource, absolute stock floor) pair: bench/recipe production
// (policy.SelectResourceMethod) first, falling back to native mine/harvest
// sources (policy.SelectResourceSources) and, if hauling them needs new
// storage, a covered stockpile zone -- see RoutineAnimalFeedPlanner for the
// MaintainAnimalFeed caller.
func (r *RoutineResourcePlanner) dispatchResourceGoal(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount], started time.Time) (RoutineResourceResult, error) {
	p := r.reviewer.player
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
		selected, sourceStorage, ok := r.sourcesForDeficit(call, identity, resource, target, stock)
		if !ok {
			return RoutineResourceResult{Reason: BuildingMethodUsed}, nil
		}
		zoneResult, handled, err := r.materialStorageZoneFallback(call, epoch, state, goal, reviewTick, resource, selected, sourceStorage, started)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if handled {
			zoneResult.Sources = selected
			return zoneResult, nil
		}
		result, dispatched, err := r.dispatchMineSource(call, epoch, state, goal, resource, selected, started)
		if err != nil {
			return RoutineResourceResult{}, err
		}
		if dispatched {
			result.Sources = selected
			return result, nil
		}
		return RoutineResourceResult{Reason: BuildingMethodUsed, Sources: selected}, nil
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
// (and its always-populated StorageCapacity payload) and applies
// policy.SelectResourceSources against the outstanding deficit, mirroring
// the first half of production_policy.py's resource_method (its bill-listing
// fallback, which SelectResourceMethod above already covers, is only reached
// once this source loop finds nothing to select). Neither this method nor
// materialStorageZoneFallback dispatches AcquireResource itself --
// dispatchMineSource is what actually acts on the selection once storage is
// adequate -- so a native read failure here is deliberately swallowed
// (ok=false) rather than surfaced, preserving the bench/recipe outcome the
// caller already computed.
func (r *RoutineResourcePlanner) sourcesForDeficit(ctx context.Context, identity *c.Identity, resource policy.Resource, target int64, stock domain.Fact[[]policy.Amount]) (selected []policy.ResourceSource, storage policy.ResourceStorage, ok bool) {
	rows, known := stock.Value()
	if !known {
		return nil, policy.ResourceStorage{}, false
	}
	var have int64
	for _, row := range rows {
		if row.Resource == resource {
			have = row.Count
			break
		}
	}
	sources, storage, _, err := r.native.ReadResourceSources(ctx, identity, string(resource))
	if err != nil {
		return nil, policy.ResourceStorage{}, false
	}
	return policy.SelectResourceSources(sources, target, have, 0), storage, true
}

// materialStorageZoneFallback ports production_policy.py's resource_method
// storage branch: once the bench/recipe path can't fund the
// dynamically-selected resource, a fresh source selection that includes a
// "mine" source is checked against the same read's StorageCapacity payload
// (NativeResourceSourcesTool.Storage) to see whether hauling that source's
// yield needs a new covered stockpile zone (policy.SelectResourceStorageZone).
// It mirrors coveredStorageFallback's exact zone-build shape
// (NewAllowListStockpileZone/PreviewZone/AdmitBuildingMethod, not
// CommitGoalMethod, since a zone carries footprint like a building), but the
// candidate cells come directly from native's own hauler-reachable, roofed,
// unreserved scan rather than policy.CoveredStorageSites -- no geometry is
// recomputed here. The zone's method ID is content-addressed by resource and
// cells (matching Python's fingerprint dedup), not attempt-numbered, since
// the candidate set is whatever native reports fresh each call, not
// something this planner deliberately retries several times per episode.
// handled is false when nothing applies this tick (no mine source selected,
// or existing capacity already covers the deficit) -- the caller should then
// fall through to dispatchMineSource instead, exactly mirroring Python's
// resource_method returning its storage zone-build action in place of an
// acquisition action only when storage is inadequate.
func (r *RoutineResourcePlanner) materialStorageZoneFallback(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, resource policy.Resource, selected []policy.ResourceSource, storage policy.ResourceStorage, started time.Time) (RoutineResourceResult, bool, error) {
	zone, needed, blocked, err := policy.SelectResourceStorageZone(selected, 0, storage)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if blocked {
		return RoutineResourceResult{Reason: BuildingMethodNoSpace}, true, nil
	}
	if !needed {
		return RoutineResourceResult{}, false, nil
	}
	p := r.reviewer.player
	held, err := p.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	protected := map[domain.Cell]bool{}
	for _, h := range held {
		for _, cell := range h.Footprint {
			protected[cell] = true
		}
	}
	cells := make([]domain.Cell, 0, len(zone.Cells))
	for _, cell := range zone.Cells {
		if !protected[cell] {
			cells = append(cells, cell)
		}
	}
	if len(cells) == 0 {
		return RoutineResourceResult{Reason: BuildingMethodNoSpace}, true, nil
	}
	value, err := domain.NewAllowListStockpileZone(domain.ImportantPriority, []string{string(resource)}, cells)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	hash := sha256.Sum256([]byte(fmt.Sprintf("%s/%v", resource, cells)))
	method := domain.MethodID(fmt.Sprintf("material-storage-%x", hash[:16]))
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineResourceResult{Reason: BuildingMethodUsed}, true, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineResourceResult{}, false, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-resource-zone-%x", digest[:16]))
	last, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	expected, err := observation.DecodeIdentity(last)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, reviewTick) {
		return RoutineResourceResult{}, false, ErrControl
	}
	reading, err := observation.ObserveColony(call, r.native, r.reviewer.clock, expected, r.reviewer.maxAge, true, nil)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	projection := reading.Projection
	token, known := projection.ZoneMapToken.Value()
	if !known {
		return RoutineResourceResult{Reason: BuildingMethodUnknown}, true, nil
	}
	snapshot := state.Snapshot
	snapshot.Plan = id
	snapshot.Revision = 1
	action, err := domain.NewZoneCreateAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	reply, _, err := r.native.PreviewZone(call, boundary.Identity(snapshot), bridge.ZoneTarget{Zone: value, Token: token})
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	v := reply.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return RoutineResourceResult{Reason: BuildingMethodRefused}, true, nil
	}
	if _, err = boundary.Context(v.Context, snapshot); err != nil || domain.Tick(v.Context.GetTick()) != projection.Identity.Tick {
		return RoutineResourceResult{}, false, ErrControl
	}
	preview := policy.Preview{Action: action, Snapshot: snapshot, Tick: projection.Identity.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), WatchCellsAccessible: domain.Known(true), Footprint: domain.Known(cells), Costs: domain.Known([]policy.Amount{})}
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
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: projection.Identity.Tick, Bounds: domain.Known(projection.Bounds), Stock: policy.StockObservation{Snapshot: snapshot, Tick: projection.Identity.Tick}, Rules: r.reviewer.rules, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	if err != nil {
		return RoutineResourceResult{}, false, err
	}
	if !decision.Admitted {
		return RoutineResourceResult{Reason: BuildingMethodRefused}, true, nil
	}
	return RoutineResourceResult{Reason: BuildingMethodAdmitted, Plan: id}, true, nil
}

// dispatchMineSource actually dispatches a domain.MineAcquisitionAction
// against the selection's mine source, if any -- the second,
// independently-registered mine-acquisition vertical this planner's mine
// dispatch needs, since a mined resource can never appear in the generic
// vertical's AcquisitionFacts census. Only a mine
// method source carries the Cell/Token bridge.ReadMineAcquisition/
// AcquireResource need (policy.SelectResourceSources populates them for
// "mine" rows only); any other selected method is left to the caller's
// observability-only Sources reporting. The caller only reaches this once
// materialStorageZoneFallback reports handled=false, i.e. either no mine
// source was selected or its covered storage already suffices. dispatched is
// false, with a zero result and nil error, when there is nothing to dispatch
// -- the caller then falls back to its own BuildingMethodUsed reporting.
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
