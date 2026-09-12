package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type RoutineBuildingSource interface {
	observation.ColonySource
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}

type RoutineBuildingReason string

const (
	BuildingMethodDisabled     RoutineBuildingReason = "disabled"
	BuildingMethodNoReview     RoutineBuildingReason = "no_current_review"
	BuildingMethodNoDeficit    RoutineBuildingReason = "no_active_deficit"
	BuildingMethodExistingWork RoutineBuildingReason = "existing_work"
	BuildingMethodUnknown      RoutineBuildingReason = "unknown_prerequisite"
	BuildingMethodNoSpace      RoutineBuildingReason = "insufficient_verified_space"
	BuildingMethodUsed         RoutineBuildingReason = "method_already_used"
	BuildingMethodRefused      RoutineBuildingReason = "shared_admission_refused"
	BuildingMethodAdmitted     RoutineBuildingReason = "admitted"
)

type RoutineBuildingResult struct {
	Reason   RoutineBuildingReason
	Decision store.BuildingMethodDecision
	// NativeWorkTicks bounds ordinary roofing or comfort use after observed construction.
	// It neither asserts recovery nor grants native authority.
	NativeWorkTicks uint32
}

// RoutineBuildingPlanner compiles one bounded building method under an
// existing reviewed player direction. It creates shared pending work, never
// acquires a lease, dispatches an action or advances the game.
type RoutineBuildingPlanner struct {
	reviewer    *RoutineReviewer
	native      RoutineBuildingSource
	goal        policy.GoalID
	definition  string
	stuff       string
	environment policy.PlacementEnvironment
	adjacent    []domain.Cell
	shelter     bool
	power       *policy.PowerProposal
}

func NewRoutineSleepingPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.EnsureInitialShelter, definition: "SleepingSpot"}, nil
}

func (r *RoutineBuildingPlanner) Step(ctx context.Context) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

// step is also used by the scheduler already holding the same player gate.
func (r *RoutineBuildingPlanner) step(call, epoch context.Context) (RoutineBuildingResult, error) {
	roofingOnly := false
	if r.shelter {
		indoor := *r
		indoor.shelter, indoor.definition = false, "SleepingSpot"
		result, err := indoor.step(call, epoch)
		if err != nil || result.Reason != BuildingMethodNoSpace && result.Reason != BuildingMethodUsed {
			return result, err
		}
		roofingOnly = result.Reason == BuildingMethodUsed
	}
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBuildingResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineBuildingResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBuildingResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	for _, binding := range review.Goals {
		if binding.Need == r.goal {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			break
		}
	}
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineBuildingResult{Reason: BuildingMethodNoDeficit}, nil
	}
	if r.goal == policy.EnsureComfort || r.goal == policy.EnsureExpansion {
		selected := false
		for _, row := range review.Development.Rows {
			selected = selected || row.Goal == r.goal && row.Selected
		}
		if !selected {
			return RoutineBuildingResult{Reason: BuildingMethodRefused}, nil
		}
	}
	for _, m := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineBuildingResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineBuildingResult{}, ErrControl
	}
	definitions := []string{r.definition}
	if r.goal == policy.EnsureComfort {
		definitions = []string{"Table1x2c", "DiningChair", "HorseshoesPin"}
	}
	if r.goal == policy.EnsureBasicPower {
		definitions = []string{"WoodFiredGenerator", "PowerConduit"}
	}
	if r.shelter {
		definitions = []string{"Wall", "Door"}
	}
	var reading observation.ColonyReading
	if r.goal == policy.EnsureComfort || r.goal == policy.EnsureBasicPower {
		full, readErr := observation.ObserveRoutine(call, r.native.(observation.RoutineSource), r.reviewer.clock, expected, r.reviewer.maxAge, definitions...)
		reading, err = full.ColonyReading, readErr
	} else {
		reading, err = observation.ObserveColony(call, r.native, r.reviewer.clock, expected, r.reviewer.maxAge, true, definitions)
	}
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	facts := reading.Projection
	if r.goal == policy.EnsureComfort || r.goal == policy.EnsureBasicPower {
		var resolved *RoutineBuildingPlanner
		var reason RoutineBuildingReason
		if r.goal == policy.EnsureBasicPower {
			resolved, reason, err = r.selectPower(facts)
		} else {
			resolved, reason, err = r.selectComfort(facts, review.Comfort)
		}
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if reason != "" {
			result := RoutineBuildingResult{Reason: reason}
			if reason == BuildingComfortWait || reason == RoutineBuildingReason(policy.PowerWaitOutput) {
				if r.goal == policy.EnsureBasicPower {
					result.NativeWorkTicks, err = powerOutputAllowance(call, p.journal, goal.Goal, state.Snapshot, facts.Identity.Tick)
				} else {
					result.NativeWorkTicks, err = comfortUseAllowance(call, p.journal, goal.Goal, state.Snapshot, facts.Identity.Tick)
				}
				if err != nil {
					return RoutineBuildingResult{}, err
				}
				if err := p.current(call, epoch); err != nil {
					return RoutineBuildingResult{}, err
				}
				if p.session.State() != state {
					return RoutineBuildingResult{}, ErrControl
				}
				last, _, err := r.native.Identity(call)
				if err != nil {
					return RoutineBuildingResult{}, err
				}
				actual, err := observation.DecodeIdentity(last)
				if err != nil || !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
					return RoutineBuildingResult{}, ErrControl
				}
				now := r.reviewer.clock.Now()
				if now.Before(reading.StartedAt) || now.Sub(reading.StartedAt) > r.reviewer.maxAge {
					return RoutineBuildingResult{}, observation.ErrStale
				}
			}
			return result, nil
		}
		r = resolved
		definitions = []string{r.definition}
	}
	missing, method, reason := r.selection(facts)
	if reason != "" {
		return RoutineBuildingResult{Reason: reason}, nil
	}
	available := routineDefinitionsAvailable(facts, definitions, r.shelter)
	if r.goal == policy.EnsureComfort || r.goal == policy.EnsureBasicPower {
		preferences, loadErr := p.journal.LoadWorkPreferences(call, state.Snapshot.Plan)
		if loadErr != nil && !errors.Is(loadErr, store.ErrNotFound) {
			return RoutineBuildingResult{}, loadErr
		}
		if preferences.Revision != review.WorkPreferenceRevision {
			return RoutineBuildingResult{}, ErrControl
		}
		available = comfortBuilderAvailable(facts, r.definition, preferences.Overrides)
	}
	if !available {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	if existing, loadErr := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); loadErr == nil {
		result := RoutineBuildingResult{Reason: BuildingMethodUsed}
		if r.shelter {
			plan, err := p.journal.LoadPlan(call, existing.Plan)
			if err != nil {
				return RoutineBuildingResult{}, err
			}
			if err := p.current(call, epoch); err != nil {
				return RoutineBuildingResult{}, err
			}
			if p.session.State() != state {
				return RoutineBuildingResult{}, ErrControl
			}
			result.NativeWorkTicks = shelterNativeWorkTicks(plan, state.Snapshot, facts.Identity.Tick)
		}
		return result, nil
	} else if !errors.Is(loadErr, store.ErrNotFound) {
		return RoutineBuildingResult{}, loadErr
	}
	if roofingOnly {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	prefix := "routine-sleep"
	if r.goal == policy.EnsureCooking {
		prefix = "routine-cook"
	}
	if r.goal == policy.EnsureBasicPower {
		prefix = "routine-power"
	}
	if r.goal == policy.EnsureComfort {
		prefix = "routine-comfort"
	}
	if r.shelter {
		prefix = "routine-shell"
	}
	planID := domain.PlanID(fmt.Sprintf("%s-%x", prefix, digest[:16]))
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	held, err := p.journal.BuildingReservations(call, snapshot)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if r.goal == policy.EnsureCooking || r.goal == policy.EnsureComfort || r.goal == policy.EnsureExpansion || r.goal == policy.EnsureBasicPower {
		pending := func(progress domain.Progress) bool {
			if r.goal == policy.EnsureBasicPower {
				return pendingFacility(progress, "PowerConduit") || pendingFacility(progress, "WoodFiredGenerator")
			}
			if r.goal == policy.EnsureExpansion {
				return pendingFacility(progress, "SleepingSpot") || pendingFacility(progress, "Bed") || pendingFacility(progress, "DoubleBed") || pendingFacility(progress, "RoyalBed")
			}
			return pendingFacility(progress, r.definition)
		}
		playerPlan, err := p.journal.LoadPlan(call, state.Snapshot.Plan)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		for _, progress := range playerPlan.Progress {
			if pending(progress) {
				return RoutineBuildingResult{Reason: BuildingMethodExistingWork}, nil
			}
		}
		for _, reservation := range held {
			if pending(reservation.Progress) {
				return RoutineBuildingResult{Reason: BuildingMethodExistingWork}, nil
			}
		}
	}
	var protected []domain.Cell
	for _, h := range held {
		v := h.Progress.View()
		effect, known := v.Effect.Value()
		if !v.Unresolved && known && (effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful) && v.Tick <= facts.Identity.Tick {
			continue
		}
		protected = append(protected, h.Footprint...)
	}
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return ErrControl
		}
		return nil
	}
	selected, stock, reason, err := r.previewMethod(call, snapshot, facts, protected, missing, check)
	if err != nil || reason != "" {
		return RoutineBuildingResult{Reason: reason}, err
	}
	last, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	actual, err := observation.DecodeIdentity(last)
	if err != nil || !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
		return RoutineBuildingResult{}, ErrControl
	}
	now := r.reviewer.clock.Now()
	if now.Before(reading.StartedAt) || now.Sub(reading.StartedAt) > r.reviewer.maxAge {
		return RoutineBuildingResult{}, observation.ErrStale
	}
	if err = check(); err != nil {
		return RoutineBuildingResult{}, err
	}
	latest, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if latest.Revision != review.Revision || !latest.Enabled {
		return RoutineBuildingResult{}, ErrControl
	}
	actions := make([]domain.Action, len(selected))
	var dependencies []domain.ActionDependency
	for i, v := range selected {
		actions[i] = v.Action
		if r.shelter && i > 0 {
			dependencies = append(dependencies, domain.ActionDependency{Action: v.Action.ID(), Requires: selected[0].Action.ID()})
		}
	}
	plan, err := domain.NewPlan(planID, 1, actions, dependencies...)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	decision, err := p.journal.AdmitBuildingMethod(call, store.BuildingMethodRequest{Goal: goal.Goal.ID, Revision: goal.Revision, Method: method, Plan: plan, Current: snapshot, Tick: facts.Identity.Tick, Bounds: domain.Known(facts.Bounds), Stock: stock, Rules: r.reviewer.rules, Previews: selected, Purpose: policy.Routine})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	reason = BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
	}
	return RoutineBuildingResult{Reason: reason, Decision: decision}, nil
}

func (r *RoutineBuildingPlanner) previewMethod(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	if r.power != nil && r.power.Method == policy.PowerConnect {
		return r.previewPowerRoute(call, snapshot, facts, protected, check)
	}
	if r.shelter {
		return r.previewShell(call, snapshot, facts, protected, check)
	}
	var cells []policy.SiteCell
	adjacent := map[domain.Cell]bool{}
	for _, c := range r.adjacent {
		adjacent[c] = true
	}
	for _, c := range facts.Cells {
		if r.definition == "DiningChair" && !adjacent[c.Cell] {
			continue
		}
		if roofed, known := c.Roofed.Value(); r.environment == policy.PlacementAnywhere || known && roofed {
			cells = append(cells, c)
		}
	}
	searchRequest := policy.PlacementSearchRequest{Snapshot: snapshot, Tick: facts.Identity.Tick, Bounds: facts.Bounds, Center: facts.Center, Cells: cells, Protected: protected, Environment: policy.PlacementIndoors, Radius: 22, Limit: 64}
	if r.power != nil {
		searchRequest.Center, searchRequest.Radius = r.power.Center, 6
	}
	if r.environment != "" {
		searchRequest.Environment = r.environment
	}
	search, err := policy.NewPlacementSearch(searchRequest)
	if err != nil {
		return nil, policy.StockObservation{}, "", err
	}
	var selected []policy.Preview
	unknownWatch := false
	usedCells := map[domain.Cell]bool{}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	for i, c := range search.Candidates() {
		if err = check(); err != nil {
			return nil, policy.StockObservation{}, "", err
		}
		b, err := domain.NewBuilding(r.definition, c, domain.North, r.stuff)
		if err != nil {
			return nil, policy.StockObservation{}, "", err
		}
		a, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), b)
		if err != nil {
			return nil, policy.StockObservation{}, "", err
		}
		preview, _, err := r.native.PreviewBuilding(call, a, snapshot)
		if err != nil {
			return nil, policy.StockObservation{}, "", err
		}
		if err = check(); err != nil {
			return nil, policy.StockObservation{}, "", err
		}
		if preview.Preview.Action != a || !preview.Stock.Snapshot.Matches(snapshot) || preview.Stock.Tick != facts.Identity.Tick {
			return nil, policy.StockObservation{}, "", ErrControl
		}
		made, known := preview.Preview.MadeFromStuff.Value()
		if r.goal == policy.EnsureComfort && r.definition == "HorseshoesPin" {
			accessible, known := preview.Preview.WatchCellsAccessible.Value()
			unknownWatch = unknownWatch || !known
			if !known || !accessible {
				continue
			}
		}
		if !known || made != (r.stuff != "") {
			return nil, policy.StockObservation{}, BuildingMethodUnknown, nil
		}
		choice, ok, err := search.Select(r.definition, r.stuff, []policy.Preview{preview.Preview})
		if err != nil {
			return nil, policy.StockObservation{}, "", err
		}
		if !ok {
			continue
		}
		footprint, _ := choice.Footprint.Value()
		overlaps := false
		for _, c := range footprint {
			overlaps = overlaps || usedCells[c]
		}
		if overlaps {
			continue
		}
		selected = append(selected, choice)
		if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 1); err != nil {
			return nil, policy.StockObservation{}, "", err
		}
		for _, c := range footprint {
			usedCells[c] = true
		}
		if int64(len(selected)) == missing {
			break
		}
	}
	if int64(len(selected)) != missing {
		if unknownWatch {
			return nil, policy.StockObservation{}, BuildingMethodUnknown, nil
		}
		return nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
	}
	return selected, stock, "", nil
}

func routineBuildingBoundary(actual observation.Identity, expected domain.GenerationSnapshot, tick domain.Tick) bool {
	paused, known := actual.Paused.Value()
	generation, generationKnown := actual.NativeGeneration.Value()
	return actual.Colony == expected.Colony && actual.Load == expected.Load && actual.Map == expected.Map && actual.Tick == tick && known && paused && generationKnown && generation == expected.Native
}
