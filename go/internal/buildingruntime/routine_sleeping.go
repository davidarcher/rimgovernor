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
	// BuildingShellBlocked: a shell begun earlier stands at the colony centre
	// but the cells it still needs are not placeable this review, or it
	// stands whole; the routine waits rather than site a second shell.
	BuildingShellBlocked    RoutineBuildingReason = "earlier_shell_blocked"
	BuildingMethodExhausted RoutineBuildingReason = "retry_bound_exhausted"
	BuildingMethodAdmitted  RoutineBuildingReason = "admitted"
	// BuildingMethodSeparation defers a butcher bill while the separated
	// butcher spot build still owns the food-supply goal.
	BuildingMethodSeparation RoutineBuildingReason = "butcher_separation_pending"
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
	reviewer      *RoutineReviewer
	native        RoutineBuildingSource
	goal          policy.GoalID
	definition    string
	stuff         string
	environment   policy.PlacementEnvironment
	adjacent      []domain.Cell
	shelter       bool
	excavation    RoutineExcavationSource
	power         *policy.PowerProposal
	temperature   *policy.TemperatureProposal
	refrigeration *policy.RefrigerationProposal
	lighting      *policy.LightingProposal
	flooring      *policy.FlooringProposal
	routes        *policy.RoutesProposal
	// facility restricts furnishing to rooms whose native role can host the
	// function; with none observed, furnishing has no verified space and the
	// same planner falls back to staging a starter shell for it.
	facility *policy.FacilityRequirement
	// workshop holds the MaintainResource pre-observation reads (deficit
	// resource, bench census, recipe catalog) for this step only.
	workshop *workshopSelection
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
	return r.step(call, epoch, newStepArbiter())
}

// step is also used by the scheduler already holding the same player gate.
func (r *RoutineBuildingPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBuildingResult, error) {
	roofingOnly := false
	if r.shelter {
		indoor := *r
		indoor.shelter, indoor.definition = false, "SleepingSpot"
		if r.goal == policy.EnsureComfort || r.goal == policy.MaintainResource || r.goal == policy.MaintainMedicalCare {
			indoor.definition = ""
		}
		result, err := indoor.step(call, epoch, arbiter)
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
	// A workshop shell is the ladder's last rung. While the initial shelter
	// is still owed, its starter shell becomes the first room, which the
	// Workshop role admits; siting a second shell beside it would split the
	// same builders across two rings (issue #4 M2 run: both rings finished
	// together, far later than one). Wait for that room instead.
	if (r.goal == policy.MaintainResource || r.goal == policy.MaintainMedicalCare) && r.shelter {
		blocked, err := initialShelterOwed(call, p, review)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if blocked {
			return RoutineBuildingResult{Reason: BuildingShellBlocked}, nil
		}
	}
	if r.goal == policy.EnsureComfort || r.goal == policy.EnsureExpansion || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes || r.goal == policy.MaintainResource {
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
	if r.goal == policy.MaintainResource {
		selection, reason, err := r.prepareWorkshop(call, state, review)
		if err != nil || reason != "" {
			return RoutineBuildingResult{Reason: reason}, err
		}
		prepared := *r
		prepared.workshop = selection
		r = &prepared
	}
	definitions := []string{r.definition}
	if r.goal == policy.EnsureComfort && !r.shelter {
		definitions = []string{"Table1x2c", "DiningChair", "HorseshoesPin"}
	}
	if r.goal == policy.MaintainResource && !r.shelter {
		definitions = r.workshop.candidates
	}
	if r.goal == policy.MaintainMedicalCare && !r.shelter {
		definitions = policy.HospitalBedDefinitions
	}
	if r.goal == policy.EnsureBasicPower {
		definitions = append([]string{"PowerConduit"}, policy.GeneratorDefinitions...)
	}
	if r.goal == policy.EnsureTemperatureSafety {
		definitions = []string{"Campfire", "PassiveCooler"}
	}
	if r.goal == policy.MaintainRefrigeration {
		definitions = []string{"Cooler"}
	}
	if r.goal == policy.MaintainLighting {
		definitions = r.lightingDefinitions()
	}
	if r.goal == policy.MaintainFlooring {
		definitions = r.flooringDefinitions()
	}
	if r.goal == policy.MaintainRoutes {
		definitions = r.routesDefinitions()
	}
	if r.shelter {
		definitions = []string{"Wall", "Door"}
	}
	var reading observation.ColonyReading
	_, routineSource := r.native.(observation.RoutineSource)
	_, roomSource := r.native.(observation.TemperatureSource)
	separation := (r.goal == policy.EnsureFoodSupply || r.goal == policy.EnsureCooking) && routineSource && roomSource
	if r.goal == policy.EnsureTemperatureSafety || r.goal == policy.EnsureComfort || r.goal == policy.MaintainResource || r.goal == policy.MaintainMedicalCare || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes || separation {
		// Cooking and butcher placements read rooms too when the source can
		// serve them, so kitchen/butcher separation protects each other's
		// rooms (issue #6 slice 2); without a census nothing is protected.
		full, readErr := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
		reading, err = full.ColonyReading, readErr
	} else if r.goal == policy.EnsureBasicPower {
		full, readErr := r.reviewer.observeOwned(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
		reading, err = full.ColonyReading, readErr
	} else {
		reading, err = r.reviewer.observeColony(call, r.native, expected, definitions)
	}
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	facts := reading.Projection
	if (r.goal == policy.EnsureComfort || r.goal == policy.MaintainResource || r.goal == policy.MaintainMedicalCare) && !r.shelter || r.goal == policy.EnsureBasicPower || r.goal == policy.EnsureTemperatureSafety || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes {
		var resolved *RoutineBuildingPlanner
		var reason RoutineBuildingReason
		var coolingAllowance uint32
		if r.goal == policy.EnsureTemperatureSafety {
			resolved, reason, err = r.selectTemperature(facts, review.Latches)
		} else if r.goal == policy.MaintainRefrigeration {
			var exhausted bool
			coolingAllowance, exhausted, err = refrigerationOutputAllowance(call, p.journal, goal.Goal, state.Snapshot, facts.Identity.Tick)
			if err != nil {
				return RoutineBuildingResult{}, err
			}
			resolved, reason, err = r.selectRefrigeration(call, facts, review.Latches, exhausted)
		} else if r.goal == policy.EnsureBasicPower {
			resolved, reason, err = r.selectPower(facts)
		} else if r.goal == policy.MaintainLighting {
			resolved, reason, err = r.selectLighting(facts, review.Latches)
		} else if r.goal == policy.MaintainFlooring {
			resolved, reason, err = r.selectFlooring(facts, review.Latches)
		} else if r.goal == policy.MaintainRoutes {
			resolved, reason, err = r.selectRoutes(facts, review.Latches)
		} else if r.goal == policy.MaintainResource {
			resolved, reason, err = r.selectWorkshop(facts)
		} else if r.goal == policy.MaintainMedicalCare {
			resolved, reason, err = r.selectHospital(facts)
		} else {
			resolved, reason, err = r.selectComfort(facts, review.Comfort)
		}
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if reason != "" {
			result := RoutineBuildingResult{Reason: reason}
			// A cooler completed on the tick the supervisor latched the
			// window reads powerOn=false until the power net ticks once, so
			// the cooling allowance also covers cooler_power_needed; a
			// cooler still unpowered when it runs out is a real hold (#66).
			powerSettling := r.goal == policy.MaintainRefrigeration && reason == RoutineBuildingReason(policy.RefrigerationPowerNeeded)
			if reason == BuildingComfortWait || reason == RoutineBuildingReason(policy.PowerWaitOutput) || reason == RoutineBuildingReason(policy.TemperatureWait) || reason == RoutineBuildingReason(policy.RefrigerationWait) || powerSettling {
				if r.goal == policy.EnsureTemperatureSafety {
					result.NativeWorkTicks, err = temperatureOutputAllowance(call, p.journal, goal.Goal, state.Snapshot, facts.Identity.Tick)
				} else if r.goal == policy.MaintainRefrigeration {
					result.NativeWorkTicks = coolingAllowance
				} else if r.goal == policy.EnsureBasicPower {
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
	if r.refrigeration != nil && r.refrigeration.Method == policy.RefrigerationSetTarget {
		return r.commitRefrigerationTarget(call, goal, func() error {
			if err := p.current(call, epoch); err != nil {
				return err
			}
			if p.session.State() != state {
				return ErrControl
			}
			return nil
		})
	}
	missing, method, reason := r.selection(facts)
	if reason != "" {
		return RoutineBuildingResult{Reason: reason}, nil
	}
	if r.shelter && r.excavation != nil {
		// An excavation project in progress under this goal epoch continues
		// stage by stage before any open-site shell is reconsidered.
		target, err := r.excavationProject(call, goal)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if target != nil {
			return r.stepExcavation(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading, target: *target})
		}
	}
	available := routineDefinitionsAvailable(facts, definitions, r.shelter)
	if (r.goal == policy.EnsureComfort || r.goal == policy.MaintainResource || r.goal == policy.MaintainMedicalCare) && !r.shelter || r.goal == policy.EnsureBasicPower || r.goal == policy.EnsureTemperatureSafety || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes {
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
		if clockSchedulerDebug {
			for _, d := range facts.Definitions {
				if d.Name == r.definition {
					clockSchedulerLog("%s: builder unavailable definition=%+v workPawns=%+v", goal.Goal.ID, d, facts.WorkPawns)
				}
			}
		}
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
	if r.goal == policy.EnsureTemperatureSafety {
		prefix = "routine-temperature"
	}
	if r.goal == policy.EnsureCooking {
		prefix = "routine-cook"
	}
	if r.goal == policy.EnsureBasicPower {
		prefix = "routine-power"
	}
	if r.goal == policy.EnsureComfort {
		prefix = "routine-comfort"
	}
	if r.goal == policy.MaintainRefrigeration {
		prefix = "routine-refrigeration"
	}
	if r.goal == policy.MaintainLighting {
		prefix = "routine-lighting"
	}
	if r.goal == policy.MaintainFlooring {
		prefix = "routine-flooring"
	}
	if r.goal == policy.MaintainRoutes {
		prefix = "routine-routes"
	}
	if r.goal == policy.MaintainResource {
		prefix = "routine-workshop"
	}
	if r.goal == policy.MaintainMedicalCare {
		prefix = "routine-hospital"
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
	if !r.shelter && (r.goal == policy.EnsureFoodSupply || r.goal == policy.EnsureCooking || r.goal == policy.EnsureComfort || r.goal == policy.MaintainResource || r.goal == policy.MaintainMedicalCare || r.goal == policy.EnsureExpansion || r.goal == policy.EnsureBasicPower || r.goal == policy.EnsureTemperatureSafety || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes) {
		pending := func(progress domain.Progress) bool {
			if r.goal == policy.EnsureTemperatureSafety {
				if r.temperature.Method == policy.TemperatureHeat {
					return pendingFacility(progress, "Campfire") || pendingFacility(progress, "Heater")
				}
				return pendingFacility(progress, "PassiveCooler") || pendingFacility(progress, "Cooler")
			}
			if r.goal == policy.EnsureBasicPower {
				for _, name := range policy.GeneratorDefinitions {
					if pendingFacility(progress, name) {
						return true
					}
				}
				return pendingFacility(progress, "PowerConduit")
			}
			if r.goal == policy.EnsureExpansion || r.goal == policy.MaintainMedicalCare {
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
	var selected []policy.Preview
	var stock policy.StockObservation
	if r.shelter && r.excavation != nil {
		var target *policy.ExcavationTarget
		selected, stock, reason, target, err = r.previewShelter(call, snapshot, facts, protected, check)
		if err == nil && target != nil {
			return r.stepExcavation(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading, target: *target})
		}
	} else {
		selected, stock, reason, err = r.previewMethod(call, snapshot, facts, protected, missing, check)
	}
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
	// A shell's walls wait for its door so the room is never sealed before it
	// has an entrance; an adopted shell whose door already stands has no such
	// gate and its walls are independent.
	doorFirst := false
	if r.shelter && len(selected) > 0 {
		if first, ok := selected[0].Action.Building(); ok {
			doorFirst = first.Definition() == "Door"
		}
	}
	for i, v := range selected {
		actions[i] = v.Action
		if doorFirst && i > 0 {
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
	return RoutineBuildingResult{Reason: reason, Decision: decision, NativeWorkTicks: stockRefusalWait(decision)}, nil
}

// stockWaitTicks bounds one clock window lent to a method refused for
// insufficient stock. The census counts what lies on the map, and a stack a
// pawn is carrying to a stockpile, a bench is about to finish or a hauler is
// about to unforbid is invisible to it; without ticks the haul never lands
// and the refusal repeats until the clock parks on no_work. The next step
// re-reads the census, so the wait is the window, not a belief about stock.
const stockWaitTicks = 2500

// stockRefusalWait is stockWaitTicks when every refusal is insufficient
// stock and zero otherwise: geometry, spending and unknown facts are not
// resolved by letting time pass.
func stockRefusalWait(decision store.BuildingMethodDecision) uint32 {
	if decision.Admitted || len(decision.Refused) == 0 {
		return 0
	}
	for _, refusal := range decision.Refused {
		if refusal.Reason != policy.InsufficientStock {
			return 0
		}
	}
	return stockWaitTicks
}

func (r *RoutineBuildingPlanner) previewMethod(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	if r.power != nil && r.power.Method == policy.PowerConnect {
		return r.previewPowerRoute(call, snapshot, facts, protected, check)
	}
	if r.refrigeration != nil {
		return r.previewRefrigeration(call, snapshot, facts, protected, check)
	}
	if r.lighting != nil {
		return r.previewLighting(call, snapshot, facts, protected, check)
	}
	if r.flooring != nil {
		return r.previewFlooring(call, snapshot, facts, protected, check)
	}
	if r.routes != nil {
		return r.previewRoutes(call, snapshot, facts, protected, check)
	}
	if r.shelter {
		return r.previewShell(call, snapshot, facts, protected, check)
	}
	if r.definition == "ButcherSpot" || r.goal == policy.EnsureCooking {
		protected = append(append([]domain.Cell(nil), protected...), policy.SeparationProtectedCells(facts.Rooms, r.definition == "ButcherSpot")...)
	}
	var cells []policy.SiteCell
	adjacent := map[domain.Cell]bool{}
	for _, c := range r.adjacent {
		adjacent[c] = true
	}
	roomCells := map[domain.Cell]bool{}
	restricted := r.temperature != nil || r.facility != nil
	if r.temperature != nil {
		for _, c := range r.temperature.Cells {
			roomCells[c] = true
		}
	}
	if r.facility != nil {
		rooms, known := facts.Rooms.Value()
		if !known {
			return nil, policy.StockObservation{}, BuildingMethodUnknown, nil
		}
		for _, c := range policy.HostingCells(*r.facility, rooms) {
			roomCells[c] = true
		}
		if len(roomCells) == 0 {
			return nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
		}
	}
	for _, c := range facts.Cells {
		if restricted && !roomCells[c.Cell] {
			continue
		}
		if r.definition == "DiningChair" && !adjacent[c.Cell] {
			continue
		}
		if roofed, known := c.Roofed.Value(); r.environment == policy.PlacementAnywhere || known && roofed {
			cells = append(cells, c)
		}
	}
	searchRequest := policy.PlacementSearchRequest{Snapshot: snapshot, Tick: facts.Identity.Tick, Bounds: facts.Bounds, Center: facts.Center, Cells: cells, Protected: append(append([]domain.Cell(nil), protected...), policy.DoorwayAisles(facts.Bounds, facts.Cells)...), Environment: policy.PlacementIndoors, Radius: 22, Limit: 64}
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

// initialShelterOwed reports whether the review binds an active
// EnsureInitialShelter goal still in deficit.
func initialShelterOwed(ctx context.Context, p *Player, review store.RoutineReview) (bool, error) {
	for _, binding := range review.Goals {
		if binding.Need != policy.EnsureInitialShelter {
			continue
		}
		goal, err := p.journal.LoadGoal(ctx, binding.Goal)
		if err != nil {
			return false, err
		}
		return goal.Goal.Status == domain.GoalActive && goal.Goal.Need == domain.NeedDeficit, nil
	}
	return false, nil
}
