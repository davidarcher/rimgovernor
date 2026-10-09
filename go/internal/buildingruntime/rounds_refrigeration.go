package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// refrigerationCoolingTicks is the native cooling allowance after a cooler
// method completes (a build or a setpoint patch): a cooler needs a while to
// pull an enclosed room down, and a second cooler is only proposed once this
// allowance has elapsed without the room reaching the exit temperature.
// refrigerationCoolingWindowTicks bounds each clock window the allowance
// lends: a cooler exchanges heat once every 250 ticks (its rare tick) and
// the review reads the stock's temperature at the window's stop, so one game
// hour per window follows the room closely enough while a two-day
// allowance still elapses in a few dozen windows.
const (
	refrigerationCoolingTicks       = 2 * domain.TicksPerDay
	refrigerationCoolingWindowTicks = domain.TicksPerHour
)

// NewRoundsRefrigerationPlanner composes MaintainRefrigeration's building
// method: cool the rooms holding warm at-risk perishable food with a Cooler
// on a vented wall, or by patching an existing cooler's setpoint.
func NewRoundsRefrigerationPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainRefrigeration) {
		return nil, fmt.Errorf("%w: NewRoundsRefrigerationPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainRefrigeration)", ErrControl)
	}
	if _, ok := native.(observation.RoundsSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsRefrigerationPlanner: !ok", ErrControl)
	}
	if _, ok := native.(observation.RefrigerationSource); !ok {
		return nil, fmt.Errorf("%w: NewRoundsRefrigerationPlanner: !ok", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainRefrigeration, definition: "Cooler"}, nil
}

// selectRefrigeration re-reviews the fresh census under the review's latch,
// reads the exact coolers and maps the policy outcome onto the planner.
func (r *RoundsBuildingPlanner) selectRefrigeration(call context.Context, facts observation.ColonyProjection, latches policy.RoundsLatches, allowance bool) (*RoundsBuildingPlanner, Verdict, error) {
	review, err := policy.ReviewRefrigeration(facts.Facts.FoodStorageUpkeep, latches.Refrigeration, r.reviewer.policy.FoodStorage)
	if err != nil {
		return nil, Verdict{}, err
	}
	review = review.WithWarmRooms(warmCoolingRooms(facts))
	if !review.Active {
		return nil, BuildingReasonNoDeficit, nil
	}
	coolers, _, err := observation.ReadRefrigerationCoolers(call, r.native.(observation.RefrigerationSource), facts.Identity, facts.PowerPlanning)
	if err != nil {
		return nil, Verdict{}, err
	}
	fact := observation.RefrigerationFacts(facts, coolers)
	proposal, err := policy.SelectRefrigerationMethod(review, fact, r.reviewer.policy.FoodStorage, allowance)
	if err != nil {
		return nil, Verdict{}, err
	}
	switch proposal.Method {
	case policy.RefrigerationBuild, policy.RefrigerationSetTarget:
		resolved := *r
		resolved.refrigeration = &proposal
		resolved.definition = "Cooler"
		return &resolved, Verdict{}, nil
	case policy.RefrigerationUnknown:
		return nil, fieldUnavailable("refrigeration"), nil
	case policy.RefrigerationNoMethod:
		return nil, BuildingReasonNoDeficit, nil
	default:
		return nil, awaitingMethod(proposal.Method), nil
	}
}

// refrigerationOutputAllowance lends bounded native cooling time after the
// latest completed cooler method; exhausted reports that such a method
// completed and its allowance has fully elapsed. An epoch with no cooler
// method of its own (a freezer that settled in an earlier epoch and
// re-latched when the season warmed, or a cooler the player set) lends the
// same allowance from the tick the latch engaged, so a second cooler can
// still be proposed once it elapses; lent reports that case.
func refrigerationOutputAllowance(ctx context.Context, journal *store.Store, goal store.WorkOwner, current domain.GenerationSnapshot, tick domain.Tick, since domain.Tick) (allowance uint32, exhausted bool, lent bool, err error) {
	methods, err := journal.LoadOwnerMethods(ctx, goal)
	if err != nil {
		return 0, false, false, err
	}
	if len(methods) == 0 {
		allowance, exhausted = refrigerationLatchAllowance(since, tick)
		return allowance, exhausted, true, nil
	}
	for _, method := range methods {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return 0, false, false, err
		}
		remaining, completed := refrigerationNativeWorkTicks(plan, current, tick)
		allowance = max(allowance, remaining)
		exhausted = exhausted || completed && remaining == 0
	}
	return allowance, exhausted && allowance == 0, false, nil
}

// refrigerationLatchAllowance is the cooling allowance measured from the
// tick the refrigeration latch engaged; nothing is lent before the latch
// has a tick or when the clock has rewound past it.
func refrigerationLatchAllowance(since, tick domain.Tick) (uint32, bool) {
	if since <= 0 || tick < since {
		return 0, false
	}
	if tick-since >= refrigerationCoolingTicks {
		return 0, true
	}
	return min(uint32(refrigerationCoolingWindowTicks), uint32(refrigerationCoolingTicks-(tick-since))), false
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
	// generation and its allowance was never lent. Same rule as
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
	return min(uint32(refrigerationCoolingWindowTicks), uint32(refrigerationCoolingTicks-(tick-v.Tick))), true
}

// previewRefrigeration previews the one exact wall cell and rotation the
// policy chose; unlike the placement search, there is no fallback cell.
func (r *RoundsBuildingPlanner) previewRefrigeration(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	if r.refrigeration == nil || r.refrigeration.Method != policy.RefrigerationBuild {
		return nil, policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}, Verdict{}, fmt.Errorf("%w: previewRefrigeration: r.refrigeration == nil || r.refrigeration.Method != policy.RefrigerationBuild", ErrControl)
	}
	return r.previewCoolerWall(ctx, snapshot, facts, protected, check, r.refrigeration.Cell, r.refrigeration.Rotation, false)
}

// previewCoolerWall previews one Cooler on the exact wall cell and rotation
// a policy chose (the refrigeration family for a food store, the
// temperature family for a sleeping room); there is no fallback cell.
// overRock previews it as though natural rock on the cell were mined, for
// the exhaust dig that mines the cell in the same plan.
func (r *RoundsBuildingPlanner) previewCoolerWall(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, check func() error, cell domain.Cell, rotation domain.Rotation, overRock bool) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	for _, c := range protected {
		if c == cell {
			return nil, stock, BuildingReasonExistingWork, nil
		}
	}
	building, err := domain.NewBuilding("Cooler", cell, rotation, "")
	if err != nil {
		return nil, stock, Verdict{}, err
	}
	previews, stock, reason, err := r.previewPlannedBuilding(ctx, snapshot, facts, building, 0, overRock, false)
	if err != nil || !reason.IsZero() {
		return nil, stock, reason, err
	}
	if footprint, _ := previews[0].Footprint.Value(); len(footprint) != 1 || footprint[0] != cell {
		return nil, stock, noSpace("cooler_wall_footprint"), nil
	}
	return previews, stock, Verdict{}, nil
}

// previewPlannedBuilding previews one planned building as action
// "<plan>-<index>"; overRock previews it as though natural rock on its
// footprint were mined. goOpen says Go's terrain facts read the building's
// cell as open: a refusal then that names no blocking thing is rock or
// terrain Go missed, an error rather than a refusal to retry.
func (r *RoundsBuildingPlanner) previewPlannedBuilding(ctx context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, building domain.Building, index int, overRock, goOpen bool) ([]policy.Preview, policy.StockObservation, Verdict, error) {
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, index)), building)
	if err != nil {
		return nil, stock, Verdict{}, err
	}
	var preview bridge.BuildingPreview
	if overRock {
		source, ok := r.native.(overRockPreviewer)
		if !ok {
			return nil, stock, fieldUnavailable("over_rock_preview"), nil
		}
		preview, _, err = source.PreviewBuildingOverRock(ctx, action, snapshot)
	} else {
		preview, _, err = r.native.PreviewBuilding(ctx, action, snapshot)
	}
	if err != nil {
		return nil, stock, Verdict{}, err
	}
	p := preview.Preview
	footprint, fk := p.Footprint.Value()
	made, mk := p.MadeFromStuff.Value()
	legal, lk := p.CanPlace.Value()
	safe, sk := p.SafeToPlace.Value()
	if !fk || !mk || !lk || !sk {
		return nil, stock, fieldUnavailable(building.Definition() + "_preview"), nil
	}
	if goOpen && !legal && len(p.Blockers) == 0 {
		return nil, stock, Verdict{}, fmt.Errorf("%w: rock step: native refused %s at %v that the frame lists open and names no blocker", ErrControl, building.Definition(), building.Cell())
	}
	if made != (building.Stuff() != "") || !slices.Contains(footprint, building.Cell()) || !legal || !safe {
		return nil, stock, noSpace("refrigeration_site"), nil
	}
	if err = mergeRoundsStock(&stock, preview.Stock, true); err != nil {
		return nil, stock, Verdict{}, err
	}
	return []policy.Preview{p}, stock, Verdict{}, nil
}

// commitRefrigerationTarget binds a one-action building-temperature plan to
// the goal, the same shape the tend planner commits: the Hands worker then
// applies the CAS-gated setpoint patch and records its receipt.
func (r *RoundsBuildingPlanner) commitRefrigerationTarget(call context.Context, goal store.WorkOwner, check func() error) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	proposal := r.refrigeration
	if proposal == nil || proposal.Method != policy.RefrigerationSetTarget {
		return RoundsBuildingResult{}, fmt.Errorf("%w: commitRefrigerationTarget: proposal == nil || proposal.Method != policy.RefrigerationSetTarget", ErrControl)
	}
	method := proposal.Key
	if _, loadErr := p.journal.LoadOwnerMethod(call, goal, method); loadErr == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "refrigeration_target")}, nil
	} else if !errors.Is(loadErr, store.ErrNotFound) {
		return RoundsBuildingResult{}, loadErr
	}
	patch, err := domain.NewBuildingTemperature(proposal.Cooler, proposal.TargetC)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewBuildingTemperatureAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = check(); err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}
