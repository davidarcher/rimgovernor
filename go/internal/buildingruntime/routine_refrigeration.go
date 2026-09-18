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

// refrigerationCoolingTicks is the native cooling allowance after a cooler
// method completes (a build or a setpoint patch): a cooler needs a while to
// pull an enclosed room down, and a second cooler is only proposed once this
// allowance has elapsed without the room reaching the exit temperature.
const refrigerationCoolingTicks = 2 * 60000

// NewRoutineRefrigerationPlanner composes MaintainRefrigeration's building
// method: cool the rooms holding warm at-risk perishable food with a Cooler
// on a vented wall, or by patching an existing cooler's setpoint.
func NewRoutineRefrigerationPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainRefrigeration) {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.TemperatureSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RefrigerationSource); !ok {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainRefrigeration, definition: "Cooler"}, nil
}

// selectRefrigeration re-reviews the fresh census under the review's latch,
// reads the exact coolers and maps the policy outcome onto the planner.
func (r *RoutineBuildingPlanner) selectRefrigeration(call context.Context, facts observation.ColonyProjection, latches policy.RoutineLatches, allowance bool) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	review, err := policy.ReviewRefrigeration(facts.Facts.FoodStorageUpkeep, latches.Refrigeration, r.reviewer.policy.FoodStorage)
	if err != nil {
		return nil, "", err
	}
	if !review.Active {
		if clockSchedulerDebug {
			clockSchedulerLog("refrigeration: inactive review=%+v storage=%+v", review, facts.Facts.FoodStorageUpkeep)
		}
		return nil, BuildingMethodNoDeficit, nil
	}
	coolers, _, err := observation.ReadRefrigerationCoolers(call, r.native.(observation.RefrigerationSource), facts.Identity, facts.PowerPlanning)
	if err != nil {
		return nil, "", err
	}
	fact := observation.RefrigerationFacts(facts, coolers)
	proposal, err := policy.SelectRefrigerationMethod(review, fact, r.reviewer.policy.FoodStorage, allowance)
	if err != nil {
		return nil, "", err
	}
	if clockSchedulerDebug {
		v, known := fact.Value()
		_, tk := facts.Rooms.Value()
		_, ck := coolers.Value()
		clockSchedulerLog("refrigeration: review=%+v factKnown=%v temperatureKnown=%v coolersKnown=%v rooms=%d cells=%d coolerAvailable=%+v proposal=%+v", review, known, tk, ck, len(v.Rooms), len(v.Cells), v.CoolerAvailable, proposal)
	}
	switch proposal.Method {
	case policy.RefrigerationBuild, policy.RefrigerationSetTarget:
		resolved := *r
		resolved.refrigeration = &proposal
		resolved.definition = "Cooler"
		return &resolved, "", nil
	case policy.RefrigerationUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.RefrigerationNoMethod:
		return nil, BuildingMethodNoDeficit, nil
	default:
		return nil, RoutineBuildingReason(proposal.Method), nil
	}
}

// refrigerationOutputAllowance lends bounded native cooling time after the
// latest completed cooler method; exhausted reports that such a method
// completed and its allowance has fully elapsed.
func refrigerationOutputAllowance(ctx context.Context, journal *store.Store, goal domain.Goal, current domain.GenerationSnapshot, tick domain.Tick) (allowance uint32, exhausted bool, err error) {
	methods, err := journal.LoadGoalMethods(ctx, goal.ID, goal.Epoch)
	if err != nil {
		return 0, false, err
	}
	for _, method := range methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return 0, false, err
		}
		remaining, completed := refrigerationNativeWorkTicks(plan, current, tick)
		allowance = max(allowance, remaining)
		exhausted = exhausted || completed && remaining == 0
	}
	return allowance, exhausted && allowance == 0, nil
}

func refrigerationNativeWorkTicks(plan store.PlanState, current domain.GenerationSnapshot, tick domain.Tick) (uint32, bool) {
	if len(plan.Progress) != 1 {
		return 0, false
	}
	progress := plan.Progress[0]
	action := progress.Action()
	if building, ok := action.Building(); ok && building.Definition() != "Cooler" {
		return 0, false
	} else if !ok {
		if _, ok := action.BuildingTemperature(); !ok {
			return 0, false
		}
	}
	current.Plan, current.Revision = plan.Spec.ID(), plan.Spec.Revision()
	v := progress.View()
	// The native generation moves with every window the supervisor stops,
	// so a cooler dispatched two windows ago never matched the current
	// generation and its allowance was never lent (#66). Same rule as
	// temperatureNativeWorkTicks: the dispatch scope, this world, any
	// generation since.
	current.Native = v.Snapshot.Native
	effect, known := v.Effect.Value()
	if v.Stage != domain.Completed || v.Unresolved || !known || effect != domain.EffectCompleted || !v.Snapshot.Matches(current) || tick < v.Tick {
		return 0, false
	}
	if tick-v.Tick >= refrigerationCoolingTicks {
		return 0, true
	}
	return min(uint32(120), uint32(refrigerationCoolingTicks-(tick-v.Tick))), true
}

// previewRefrigeration previews the one exact wall cell and rotation the
// policy chose; unlike the placement search, there is no fallback cell.
func (r *RoutineBuildingPlanner) previewRefrigeration(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	if r.refrigeration == nil || r.refrigeration.Method != policy.RefrigerationBuild {
		return nil, stock, "", ErrControl
	}
	cell := r.refrigeration.Cell
	for _, c := range protected {
		if c == cell {
			return nil, stock, BuildingMethodExistingWork, nil
		}
	}
	building, err := domain.NewBuilding("Cooler", cell, r.refrigeration.Rotation, "")
	if err != nil {
		return nil, stock, "", err
	}
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", snapshot.Plan)), building)
	if err != nil {
		return nil, stock, "", err
	}
	preview, _, err := r.native.PreviewBuilding(ctx, action, snapshot)
	if err != nil {
		return nil, stock, "", err
	}
	if err = check(); err != nil {
		return nil, stock, "", err
	}
	p := preview.Preview
	if p.Action != action || !p.Snapshot.Matches(snapshot) || p.Tick != facts.Identity.Tick || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
		return nil, stock, "", ErrControl
	}
	footprint, fk := p.Footprint.Value()
	made, mk := p.MadeFromStuff.Value()
	legal, lk := p.CanPlace.Value()
	safe, sk := p.SafeToPlace.Value()
	if !fk || !mk || !lk || !sk {
		return nil, stock, BuildingMethodUnknown, nil
	}
	if made || len(footprint) != 1 || footprint[0] != cell || !legal || !safe {
		return nil, stock, BuildingMethodNoSpace, nil
	}
	if err = mergeRoutineStock(&stock, preview.Stock, true); err != nil {
		return nil, stock, "", err
	}
	if clockSchedulerDebug {
		clockSchedulerLog("refrigeration: preview costs=%+v stock=%+v", p.Costs, stock.Values)
	}
	return []policy.Preview{p}, stock, "", nil
}

// commitRefrigerationTarget binds a one-action building-temperature plan to
// the goal, the same shape the tend planner commits: the Hands worker then
// applies the CAS-gated setpoint patch and records its receipt.
func (r *RoutineBuildingPlanner) commitRefrigerationTarget(call context.Context, goal store.GoalState, check func() error) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	proposal := r.refrigeration
	if proposal == nil || proposal.Method != policy.RefrigerationSetTarget {
		return RoutineBuildingResult{}, ErrControl
	}
	method := proposal.Key
	if _, loadErr := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); loadErr == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(loadErr, store.ErrNotFound) {
		return RoutineBuildingResult{}, loadErr
	}
	patch, err := domain.NewBuildingTemperature(proposal.Cooler, proposal.TargetC, proposal.Token)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-refrigeration-%x", digest[:16]))
	action, err := domain.NewBuildingTemperatureAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err = check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	return RoutineBuildingResult{Reason: BuildingMethodAdmitted}, nil
}
