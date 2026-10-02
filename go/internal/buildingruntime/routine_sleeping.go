package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
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
	// BuildingBunksOpen: the initial shelter's indoor furnishing waits on
	// its open bunk rungs, which do not hold the ring itself (#641).
	BuildingBunksOpen     RoutineBuildingReason = "shelter_bunks_open"
	BuildingMethodUnknown RoutineBuildingReason = "unknown_prerequisite"
	BuildingMethodNoSpace RoutineBuildingReason = "insufficient_verified_space"
	BuildingMethodUsed    RoutineBuildingReason = "method_already_used"
	BuildingMethodRefused RoutineBuildingReason = "shared_admission_refused"
	// BuildingMethodNoSquad is the defense planner's answer when live
	// threats remain and no eligible squad can be assigned to them (#326).
	BuildingMethodNoSquad RoutineBuildingReason = "no_eligible_squad"
	// BuildingMethodHoldFallback: the fight's hold-the-line formation was
	// re-formed as squad defense because the raid crossed the line (#118).
	BuildingMethodHoldFallback RoutineBuildingReason = "hold_fallback"
	// BuildingMethodCombatOrders: the fight's stop sent changed orders
	// (#852), recorded as its plan's evidence.
	BuildingMethodCombatOrders RoutineBuildingReason = "combat_orders"
	// BuildingShellBlocked: a shell begun earlier stands at the colony centre
	// but the cells it still needs are not placeable this review, or it
	// stands whole without a finished room inside; the routine waits rather
	// than site a second shell. A facility ladder at a whole ring that
	// already encloses a room sites afresh instead (#218).
	BuildingShellBlocked    RoutineBuildingReason = "earlier_shell_blocked"
	BuildingMethodExhausted RoutineBuildingReason = "retry_bound_exhausted"
	// BuildingShelterPending: the foothold comfort goal has no room to
	// furnish until the initial shelter stands; the next review re-reads it.
	BuildingShelterPending RoutineBuildingReason = "initial_shelter_pending"
	// BuildingExcavationBlocked: the goal's ore tunnel cannot be
	// finished as planned (its roof no longer held, its way in closed); the
	// tunnel is re-sited from the geometry the pawns opened.
	BuildingExcavationBlocked RoutineBuildingReason = "excavation_blocked"
	BuildingMethodAdmitted    RoutineBuildingReason = "admitted"
	// BuildingMethodSeparation defers a butcher bill while the separated
	// butcher spot build still owns the food-supply goal.
	BuildingMethodSeparation RoutineBuildingReason = "butcher_separation_pending"
	// BuildingMethodNotInteractive: the choice dialog's own interactivity
	// delay has not elapsed; the next review re-reads it.
	BuildingMethodNotInteractive RoutineBuildingReason = "dialog_not_interactive"
	// BuildingMethodResearch: the goal's only method needs a native research
	// project the census has not finished; the reason names it
	// ("waiting_on_research:Electricity") and EnsureResearch's roadmap is
	// what gets there (#230).
	BuildingMethodResearch RoutineBuildingReason = "waiting_on_research"
)

func researchWaitReason(project string) RoutineBuildingReason {
	return BuildingMethodResearch + ":" + RoutineBuildingReason(project)
}

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
	paste            []policy.SiteBuilding
	reviewer         *RoutineReviewer
	native           RoutineBuildingSource
	goal             policy.GoalID
	definition       string
	recreationPowerW float64
	stuff            string
	environment      policy.PlacementEnvironment
	adjacent         []domain.Cell
	shelter          bool
	excavation       RoutineExcavationSource
	power            *policy.PowerProposal
	temperature      *policy.TemperatureProposal
	refrigeration    *policy.RefrigerationProposal
	lighting         *policy.LightingProposal
	flooring         *policy.FlooringProposal
	// firebreakPave supplies MaintainFlooring's firebreak tier: the ring's
	// pave cells still natural ground with no floor ordered (#1549).
	firebreakPave func() []domain.Cell
	routes        *policy.RoutesProposal
	// facility restricts furnishing to rooms whose native role can host the
	// function; with none observed, furnishing has no verified space and the
	// same planner falls back to staging a starter shell for it.
	facility *policy.FacilityRequirement
	// cells further restricts furnishing to these observed cells (the
	// sleeping planner's warm hosting rooms); nil imposes no restriction.
	cells []domain.Cell
	// workshop holds the MaintainResource pre-observation reads (deficit
	// resource, bench census, recipe catalog) for this step only.
	workshop *workshopSelection
	// sleeping holds the housing bedroom choice this step builds for.
	sleeping *policy.SleepingChoice
	// bedroom is the planned bedroom a housing bedroom step furnishes (#786).
	bedroom *policy.BedroomStep
	// phase is the step of a phased goal (MaintainHousing, EnsureComfort) this
	// planner serves; the review's latched phase decides which one runs.
	// Empty for every other goal.
	phase policy.Phase
}

func NewRoutineSleepingPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineSleepingPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainHousing, phase: policy.HousingShelter, definition: "SleepingSpot"}, nil
}

// step is also used by the scheduler already holding the same player gate.
func (r *RoutineBuildingPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBuildingResult, error) {
	roofingOnly := false
	if r.shelter {
		indoor := *r
		indoor.shelter, indoor.definition = false, "SleepingSpot"
		if r.facilityLadder() {
			indoor.definition = ""
		}
		result, err := indoor.step(call, epoch, arbiter)
		clockSchedulerLog("%s: indoor step reason=%v err=%v", r.goal, result.Reason, err)
		if err != nil || result.Reason != BuildingMethodNoSpace && result.Reason != BuildingMethodUsed && result.Reason != BuildingBunksOpen {
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
		return RoutineBuildingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBuildingResult{Reason: BuildingMethodNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, r.goal)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !workable {
		return RoutineBuildingResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// A phased goal walks its phases in order: only the planner for the
	// phase the review left owed runs.
	if r.phase != "" && review.Latches.Phase(r.goal) != r.phase {
		return RoutineBuildingResult{Reason: BuildingMethodNoDeficit}, nil
	}
	// A facility shell is the ladder's last rung. While the initial shelter
	// is still owed, its starter shell becomes the first room, which the
	// Workshop, Hospital and Dining roles admit; siting a second shell beside
	// it would split the same builders across two rings (issue #4 M2 run:
	// both rings finished together, far later than one). Wait for that room
	// instead. Comfort reaches this rung once the starter shell is on record
	// (development no longer holds it for the whole startup ladder, #196).
	if r.facilityLadder() && r.shelter {
		blocked, err := initialShelterOwed(call, p, review)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if blocked {
			return RoutineBuildingResult{Reason: BuildingShellBlocked}, nil
		}
	}
	if r.phase == policy.ComfortBasic {
		owed, err := initialShelterOwed(call, p, review)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if owed {
			return RoutineBuildingResult{Reason: BuildingShelterPending}, nil
		}
	}
	if r.phase == policy.ComfortRanked || r.phase == policy.HousingExpansion || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes || (r.goal == policy.MaintainResource || r.goal == policy.MaintainEquipment) || r.phase == policy.HousingSleeping {
		if !developmentSelects(review.Development.Rows, r.goal) {
			return RoutineBuildingResult{Reason: BuildingMethodRefused}, nil
		}
	}
	bunksOpen := false
	for _, m := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if r.goal == policy.EnsureFoodSupply {
			// Fields, foraging, hunts and bills share the goal and stay
			// open for days; only a pending spot of this definition is
			// the butcher planner's own work (#260).
			for _, progress := range plan.Progress {
				if pendingFacility(progress, r.definition) {
					return RoutineBuildingResult{Reason: BuildingMethodExistingWork}, nil
				}
			}
			continue
		}
		if r.phase == policy.HousingShelter && (m.Method == shelterSpotsMethod || m.Method == shelterBedsMethod) {
			// An open bunk rung does not hold the ring (#641): the walls and
			// door stand on cells the bunks never take (the ring is sited
			// around them), and a stalled bed must not keep the colony
			// outdoors. The rungs and the shell each stay idempotent on
			// their own method binding. The indoor furnishing step still
			// waits for the bunks, and says so, so the shell step runs.
			if !r.shelter && store.PlanOpen(plan) {
				bunksOpen = true
			}
			continue
		}
		if store.PlanOpen(plan) {
			return RoutineBuildingResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	if bunksOpen {
		return RoutineBuildingResult{Reason: BuildingBunksOpen}, nil
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
		// ErrControl alone reads as a lost writer gate in the diagnosis
		// (#662); name the boundary that actually failed.
		clockSchedulerLog("%s: ErrControl identity boundary observed=%+v tick=%d review tick=%d snapshot=%+v", r.goal, expected, expected.Tick, review.Tick, state.Snapshot)
		return RoutineBuildingResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	if r.goal == policy.MaintainResource || r.goal == policy.MaintainEquipment {
		var recorded func()
		call, recorded = recordPlannerStep(call, r.goal, state.Snapshot, review.Tick)
		defer recorded()
		selection, reason, err := r.prepareWorkshop(call, state, review)
		if err != nil || reason != "" {
			return RoutineBuildingResult{Reason: reason}, err
		}
		prepared := *r
		prepared.workshop = selection
		r = &prepared
	}
	definitions := []string{r.definition}
	if r.goal == policy.EnsureCooking {
		definitions = []string{"Campfire", "NutrientPasteDispenser", "Hopper"}
	}
	if r.phase == policy.ComfortRanked && !r.shelter || r.phase == policy.ComfortBasic {
		definitions = []string{"Table1x2c", "DiningChair", "HorseshoesPin"}
	}
	if r.phase == policy.ComfortBasic {
		definitions = append(definitions, "TubeTelevision", "BilliardsTable", "ChessTable")
	}
	if (r.goal == policy.MaintainResource || r.goal == policy.MaintainEquipment) && !r.shelter {
		definitions = r.workshop.candidates
	}
	if r.goal == policy.MaintainMedicalReserves && !r.shelter {
		definitions = policy.HospitalBedDefinitions
	}
	if r.phase == policy.HousingSleeping && !r.shelter {
		definitions = policy.SleepingLadder(true)
	}
	if r.goal == policy.EnsureResearch && !r.shelter {
		definitions = []string{policy.ResearchBenchDefinition}
	}
	var pendingConsumers []string
	if r.goal == policy.EnsureBasicPower {
		// The consumers other goals' admitted plans are about to build join
		// the census request so the budget can price their draw.
		held, err := p.journal.BuildingReservations(call, state.Snapshot)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		pendingConsumers = pendingBuildingDefinitions(held)
		definitions = append(append([]string{"Wall", "Door"}, policy.PowerFamilyDefinitions()...), pendingConsumers...)
	}
	if r.goal == policy.EnsureTemperatureSafety {
		definitions = temperatureDefinitions
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
	// The read names the definitions the step may place; the initial
	// shelter also places its bunks before the ring (#612), and their
	// availability is judged from the same read.
	observed := definitions
	if r.shelter && r.phase == policy.HousingShelter {
		observed = append(append([]string(nil), definitions...), "SleepingSpot", shelterBedDefinition, policy.SleepingBedrollDefinition)
	}
	if r.goal == policy.EnsureCooking || r.goal == policy.MaintainRefrigeration {
		// The planned kitchen or freezer is shelled first (#835).
		observed = append(append([]string(nil), observed...), "Wall", "Door")
	}
	if r.shelter {
		// The door ladder proposes an Autodoor only once the read shows it
		// available (#610); the ring never waits on it.
		observed = append(append([]string(nil), observed...), "Autodoor")
	}
	var reading observation.ColonyReading
	_, routineSource := r.native.(observation.RoutineSource)
	separation := (r.goal == policy.EnsureFoodSupply || r.goal == policy.EnsureCooking) && routineSource
	if r.goal == policy.EnsureTemperatureSafety || r.facilityLadder() || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes || separation {
		// Cooking and butcher placements read rooms too when the source can
		// serve them, so kitchen/butcher separation protects each other's
		// rooms (issue #6 slice 2); without a census nothing is protected.
		full, readErr := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim](), observed...)
		reading, err = full.ColonyReading, readErr
	} else if r.goal == policy.EnsureBasicPower || r.phase == policy.ComfortBasic {
		full, readErr := r.reviewer.observeOwned(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim](), observed...)
		reading, err = full.ColonyReading, readErr
	} else {
		reading, err = r.reviewer.observeColony(call, r.native, expected, observed)
	}
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	facts := reading.Projection
	recordStepRead("building", r.goal, state.Snapshot, facts)
	if r.goal == policy.EnsureCooking {
		// A cooking campfire in a sleeping room, or one a stove kitchen
		// supersedes, is deconstructed first (#1179).
		claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if known, ok := claims.Value(); ok {
			if campfire, owed := campfireRetirement(facts, known); owed {
				result, err := r.retireCampfire(call, epoch, state, review, goal, reading, campfire)
				if err != nil || result.Reason != BuildingMethodUsed {
					return result, err
				}
			}
		}
		resolved, reason := r.selectPaste(facts)
		if reason != "" {
			return RoutineBuildingResult{Reason: reason}, nil
		}
		r = resolved
	}
	var coolingAllowance uint32
	var coolingLent bool
	if r.facilityLadder() && !r.shelter || r.phase == policy.ComfortBasic || r.goal == policy.EnsureBasicPower || r.goal == policy.EnsureTemperatureSafety || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes {
		var resolved *RoutineBuildingPlanner
		var reason RoutineBuildingReason
		if r.goal == policy.EnsureTemperatureSafety {
			resolved, reason, err = r.selectTemperature(facts, review.Latches)
		} else if r.goal == policy.MaintainRefrigeration {
			var exhausted bool
			coolingAllowance, exhausted, coolingLent, err = refrigerationOutputAllowance(call, p.journal, goal.Goal, state.Snapshot, facts.Identity.Tick, review.Latches.RefrigerationSince)
			if err != nil {
				return RoutineBuildingResult{}, err
			}
			// The dining room's meal closet stands before its cooler
			// (#936); a shell already tried this epoch, or refused, leaves
			// the cooling to go on.
			if closet, owed := plannedMealCloset(facts); positiveFact(owed) {
				result, err := r.shellRoom(call, epoch, state, review, goal, reading, closet, plannedRoomMethod(closet), "cold meal shelf")
				if err != nil || result.Reason != BuildingMethodUsed && result.Reason != BuildingMethodNoSpace && result.Reason != BuildingMethodUnknown {
					return result, err
				}
			}
			resolved, reason, err = r.selectRefrigeration(call, facts, review.Latches, exhausted)
		} else if r.goal == policy.EnsureBasicPower {
			resolved, reason, err = r.selectPower(facts, pendingConsumers)
		} else if r.goal == policy.MaintainLighting {
			resolved, reason, err = r.selectLighting(facts, review.Latches)
		} else if r.goal == policy.MaintainFlooring {
			resolved, reason, err = r.selectFlooring(facts, review.Latches)
		} else if r.goal == policy.MaintainRoutes {
			resolved, reason, err = r.selectRoutes(facts, review.Latches)
		} else if r.goal == policy.MaintainResource || r.goal == policy.MaintainEquipment {
			resolved, reason, err = r.selectWorkshop(call, state, review, facts)
		} else if r.goal == policy.MaintainMedicalReserves {
			resolved, reason, err = r.selectHospital(facts)
		} else if r.goal == policy.EnsureResearch {
			resolved, reason, err = r.selectResearchBench(facts)
		} else if r.phase == policy.HousingSleeping {
			resolved, reason, err = r.selectSleeping(facts, review)
		} else if r.phase == policy.ComfortBasic {
			resolved, reason, err = r.selectBasicComfort(facts)
		} else {
			resolved, reason, err = r.selectComfort(facts, review.Comfort)
		}
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if reason != "" {
			result := RoutineBuildingResult{Reason: reason}
			if r.goal == policy.EnsureBasicPower && reason == BuildingMethodNoSpace {
				result.NativeWorkTicks, err = powerOutputAllowance(call, p.journal, goal.Goal, state.Snapshot, facts.Identity.Tick)
				return result, err
			}
			if r.goal == policy.EnsureBasicPower && reason == RoutineBuildingReason(policy.PowerWaitFuel) {
				// An empty generator is refuelled by a native haul the
				// census cannot see coming; like a stock refusal, the
				// hold lends the window that haul needs rather than
				// parking the clock on no_work.
				result.NativeWorkTicks = stockWaitTicks
				return result, nil
			}
			// A cooler completed on the tick the supervisor latched the
			// window reads powerOn=false until the power net ticks once, so
			// the cooling allowance also covers cooler_power_needed; a
			// cooler still unpowered when it runs out is a real hold (#66).
			// Time lent from the latch alone does not cover it: with no
			// method in the epoch there is no cooler still settling (#202).
			powerSettling := r.goal == policy.MaintainRefrigeration && reason == RoutineBuildingReason(policy.RefrigerationPowerNeeded) && !coolingLent
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
					return RoutineBuildingResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
				}
				last, _, err := r.native.Identity(call)
				if err != nil {
					return RoutineBuildingResult{}, err
				}
				actual, err := observation.DecodeIdentity(last)
				if err != nil || !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
					return RoutineBuildingResult{}, fmt.Errorf("%w: step: err != nil || !routineBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick)", ErrControl)
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
	if r.temperature != nil && r.temperature.Thing != "" {
		return r.commitCampfireRefuel(call, goal, func() error {
			if err := p.current(call, epoch); err != nil {
				return err
			}
			if p.session.State() != state {
				return fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
			}
			return nil
		})
	}
	if r.refrigeration != nil && r.refrigeration.Method == policy.RefrigerationSetTarget {
		result, err := r.commitRefrigerationTarget(call, goal, func() error {
			if err := p.current(call, epoch); err != nil {
				return err
			}
			if p.session.State() != state {
				return fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
			}
			return nil
		})
		// A setpoint patch that landed on a paused game leaves the census
		// reading the old target until the game ticks (the review re-reads
		// on tick advance), so the same patch is proposed again and found
		// used. Lend the cooling allowance anyway: the clock then runs, the
		// census refreshes and the room cools.
		if err == nil && result.Reason == BuildingMethodUsed {
			result.NativeWorkTicks = coolingAllowance
		}
		return result, err
	}
	missing, method, reason := r.selection(facts)
	if reason != "" {
		return RoutineBuildingResult{Reason: reason}, nil
	}
	if module, ok := r.plannedRoomModule(); ok {
		// The planned room stands before its stove or cooler (#835); a
		// shell already tried this epoch, or refused, leaves the usual
		// placement to go on.
		if room, owed := plannedRoomOwed(facts, module); owed {
			result, err := r.shellRoom(call, epoch, state, review, goal, reading, room, plannedRoomMethod(room), "")
			if err != nil || result.Reason != BuildingMethodUsed && result.Reason != BuildingMethodNoSpace && result.Reason != BuildingMethodUnknown {
				return result, err
			}
		} else if module == policy.ModuleKitchen {
			if cells := plannedRoomCells(facts, module); cells != nil {
				kitchen := *r
				kitchen.cells = cells
				r = &kitchen
			}
		}
	}
	if r.goal == policy.EnsureCooking {
		definitions = []string{r.definition}
		if len(r.paste) > 0 {
			definitions = append(definitions, "Hopper")
		}
	}
	available := routineDefinitionsAvailable(facts, definitions, r.shelter)
	if len(r.paste) > 0 {
		available = true
		for _, name := range definitions {
			available = available && comfortBuilderAvailable(facts, name)
		}
	}
	if r.facilityLadder() && !r.shelter || r.phase == policy.ComfortBasic || r.goal == policy.EnsureBasicPower || r.goal == policy.EnsureTemperatureSafety || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes {
		available = comfortBuilderAvailable(facts, r.definition)
		if r.power != nil && r.power.Method == policy.PowerGenerate {
			// A generator no site accepts yields to the next ranked one a
			// builder here can raise.
			var alternatives []string
			for _, name := range r.power.Alternatives {
				if comfortBuilderAvailable(facts, name) {
					alternatives = append(alternatives, name)
				}
			}
			r.power.Alternatives = alternatives
		}
	}
	if !available {
		if clockDebug() {
			for _, d := range facts.Definitions {
				if d.Name == r.definition {
					clockSchedulerLog("%s: builder unavailable definition=%+v workPawns=%+v", goal.Goal.ID, d, facts.WorkPawns)
				}
			}
		}
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	if r.shelter {
		// A shell plan that settled with a cell unsuccessful (a wall the
		// player cancelled in-game, a frame that failed) leaves a gap in the
		// ring, and a suspended-and-resumed goal keeps its epoch, so nobody
		// would ever reorder the cell. Walk the shell's repair chain: the
		// latest bound plan still working, or whole, is the method in use;
		// one settled with a gap hands its method to the next repair, which
		// the adoption path below fills from the ring on record.
		repaired, used, err := r.shellRepairMethod(call, goal, method)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if used != nil {
			if err := p.current(call, epoch); err != nil {
				return RoutineBuildingResult{}, err
			}
			if p.session.State() != state {
				return RoutineBuildingResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
			}
			return RoutineBuildingResult{Reason: BuildingMethodUsed, NativeWorkTicks: shelterNativeWorkTicks(*used, state.Snapshot, facts.Identity.Tick)}, nil
		}
		method = repaired
	} else {
		// A campfire the pawns let burn out leaves the cooking census empty
		// again in the same epoch; the completed method yields to a numbered
		// successor the same way a staged bed's does (#217).
		// Indoor shelter spots do too once the census shows fewer regular
		// beds than the colony needs: a spot converted to medical (which
		// native indoor capacity excludes) or lost must not leave a spent
		// method holding the shelter gate failed. Spots that stand but are
		// not yet counted (an open roof) keep the method used.
		if r.phase == policy.HousingSleeping || r.goal == policy.EnsureCooking || r.phase == policy.HousingShelter && regularBedsShort(facts.Facts) {
			if method, err = r.nextSleepingBedMethod(call, goal, method, facts.Facts.CurrentConstruction); err != nil {
				return RoutineBuildingResult{}, err
			}
		}
		if _, loadErr := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); loadErr == nil {
			return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
		} else if !errors.Is(loadErr, store.ErrNotFound) {
			return RoutineBuildingResult{}, loadErr
		}
	}
	if roofingOnly {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	held, err := p.journal.BuildingReservations(call, snapshot)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !r.shelter && (r.goal == policy.EnsureFoodSupply || r.goal == policy.EnsureCooking || r.facilityLadder() || r.phase == policy.ComfortBasic || r.phase == policy.HousingExpansion || r.goal == policy.EnsureBasicPower || r.goal == policy.EnsureTemperatureSafety || r.goal == policy.MaintainRefrigeration || r.goal == policy.MaintainLighting || r.goal == policy.MaintainFlooring || r.goal == policy.MaintainRoutes) {
		pending := func(progress domain.Progress) bool {
			if r.goal == policy.EnsureTemperatureSafety {
				if r.temperature.Method == policy.TemperatureHeat {
					return pendingFacility(progress, "Campfire") || pendingFacility(progress, "Heater")
				}
				return pendingFacility(progress, "PassiveCooler") || pendingFacility(progress, "Cooler")
			}
			if r.goal == policy.EnsureBasicPower {
				for _, name := range policy.PowerFamilyDefinitions() {
					if pendingFacility(progress, name) {
						return true
					}
				}
				return pendingFacility(progress, "PowerConduit") || pendingFacility(progress, "HiddenConduit") || pendingFacility(progress, "WaterproofConduit")
			}
			if r.phase == policy.HousingExpansion || r.goal == policy.MaintainMedicalReserves || r.phase == policy.HousingSleeping {
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
	// Building intents admitted on other goals' plans but not yet applied
	// show nothing on the map; keep off their anchors (#943).
	var own []domain.PlanID
	for _, m := range goal.Methods {
		own = append(own, m.Plan)
	}
	pendingAnchors, err := p.journal.PendingBuildingAnchors(call, snapshot, own)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	protected = append(protected, pendingAnchors...)
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
		}
		return nil
	}
	var selected []policy.Preview
	var stock policy.StockObservation
	if r.shelter {
		// The shelter's siting and dig decisions, recorded for replay (#745).
		var finish func(domain.GenerationSnapshot, domain.Tick) error
		call, finish = snap.StartPlanner(call, r.goal)
		defer func() {
			if err := finish(snapshot, facts.Identity.Tick); err != nil {
				clockSchedulerLog("%s: planner snapshot not recorded: %v", r.goal, err)
			}
		}()
	}
	if r.shelter && r.phase != policy.HousingShelter {
		if result, handled, err := r.prepareShellRoom(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading}, protected, check); err != nil || handled {
			return result, err
		}
	}
	if r.refrigeration != nil && r.refrigeration.Method == policy.RefrigerationBuild {
		if result, handled, err := r.digExhaust(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading}, check); err != nil || handled {
			return result, err
		}
	}
	if r.shelter && r.phase == policy.HousingShelter {
		// The starter shell is raised around its bunks (#612): the sleeping
		// spots and then the beds are placed on the site first, each a rung
		// under this goal, and the ring follows once the beds stand.
		var handled *RoutineBuildingResult
		selected, stock, reason, handled, err = r.stepShelterSite(call, epoch, shelterSite{state: state, review: review, goal: goal, facts: facts, read: reading, snapshot: snapshot, protected: protected, check: check})
		if err != nil || handled != nil {
			if handled == nil {
				handled = &RoutineBuildingResult{}
			}
			return *handled, err
		}
	} else {
		selected, stock, reason, err = r.previewMethod(call, snapshot, facts, protected, missing, check)
	}
	if err != nil || reason != "" {
		return RoutineBuildingResult{Reason: reason}, err
	}
	// Admission never checks stock: RimWorld places the blueprints
	// regardless and the frames hold natively for materials, which
	// MaintainResource then reads as the deficit (#602).
	purpose := policy.Routine
	if r.shelter || r.power != nil && r.power.Method == policy.PowerShelter {
		purpose = policy.Shelter
	}
	return r.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: purpose})
}

// routineAdmission is what admitPreviews commits: the previews a method
// selected under one observation, bound to the goal as that method.
type routineAdmission struct {
	state  ControlState
	review store.RoutineReview
	goal   store.GoalState
	facts  observation.ColonyProjection
	method domain.MethodID
	// reason is the planner's short why, stored with the method (#846).
	reason   string
	snapshot domain.GenerationSnapshot
	selected []policy.Preview
	stock    policy.StockObservation
	purpose  policy.Purpose
}

// admitPreviews admits the plan, the previews in dispatch order. Native
// validates each building intent when it applies it (#856), so the siting
// reads carry no boundary re-check here.
func (r *RoutineBuildingPlanner) admitPreviews(call, epoch context.Context, a routineAdmission) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	actions := make([]domain.Action, len(a.selected))
	// A shell goes out as one wave with its door first in dispatch order
	// (the preview lists it first). The walls are not gated on the door
	// completing: a door blueprint or frame no more seals a room than a
	// wall's does, and gating held every wall until the door stood, or for
	// ever when its observation came back unknown (#602). The pen shell in
	// routine_animal_containment.go orders its ring the same way.
	for i, v := range a.selected {
		actions[i] = v.Action
	}
	plan, err := domain.NewPlan(a.snapshot.Plan, a.snapshot.Revision, actions)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Goal: a.goal.Goal.ID, Revision: a.goal.Revision, Method: a.method, Reason: a.reason, Plan: plan, Current: a.snapshot, Tick: a.facts.Identity.Tick, Bounds: domain.Known(a.facts.Bounds), Stock: a.stock, Previews: a.selected, Purpose: a.purpose})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	reason := BuildingMethodRefused
	if decision.Admitted {
		reason = BuildingMethodAdmitted
		// A shell admitted short of a material (#602) records each
		// shortfall so the ranking orders that resource's acquisition ahead
		// of unrelated optional work (#651, #728). The review keeps an edge
		// only while its actions stay open.
		if a.purpose == policy.Shelter {
			for _, resource := range previewResources(a.selected) {
				if rec, short := store.ShortfallDependency(r.goal, decision.Goal.Goal, a.method, plan.ID(), a.selected, a.stock, resource, a.facts.Identity.Tick); short {
					if err = p.journal.RecordDependency(call, a.review.Revision, rec); err != nil && !errors.Is(err, store.ErrConflict) {
						return RoutineBuildingResult{}, err
					}
				}
			}
		}
	}
	return RoutineBuildingResult{Reason: reason, Decision: decision}, nil
}

// stockWaitTicks bounds one clock window lent to a method refused for
// insufficient stock. The census counts what lies on the map, and a stack a
// pawn is carrying to a stockpile, a bench is about to finish or a hauler is
// about to unforbid is invisible to it; without ticks the haul never lands
// and the refusal repeats until the clock parks on no_work. The next step
// re-reads the census, so the wait is the window, not a belief about stock.
const stockWaitTicks = 2500

func (r *RoutineBuildingPlanner) previewMethod(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	if r.power != nil && r.power.Method == policy.PowerShelter {
		return r.previewPowerShelter(call, snapshot, facts, protected, check)
	}
	if r.power != nil && r.power.Method == policy.PowerConnect {
		return r.previewPowerRoute(call, snapshot, facts, protected, check)
	}
	if r.power != nil && r.power.FixedSite() {
		return r.previewPowerSite(call, snapshot, facts, protected, check)
	}
	selected, stock, reason, err := r.previewPowerOrSearch(call, snapshot, facts, protected, missing, check)
	if r.power != nil && r.power.Method == policy.PowerGenerate {
		// A generator with no acceptable site near the consumer (a wind
		// turbine whose every catch zone is obstructed) yields to the next
		// ranked definition under the same method.
		for _, name := range r.power.Alternatives {
			if err != nil || reason != BuildingMethodNoSpace {
				break
			}
			next := *r
			next.definition = name
			selected, stock, reason, err = next.previewPowerOrSearch(call, snapshot, facts, protected, missing, check)
		}
	}
	return selected, stock, reason, err
}

func (r *RoutineBuildingPlanner) previewSearch(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, RoutineBuildingReason, error) {
	if len(r.paste) > 0 {
		return r.previewPaste(call, snapshot, facts, protected, check)
	}
	if r.refrigeration != nil {
		return r.previewRefrigeration(call, snapshot, facts, protected, check)
	}
	if r.temperature != nil && r.temperature.Method == policy.TemperatureCoolPowered {
		return r.previewCoolerWall(call, snapshot, facts, protected, check, r.temperature.Cell, r.temperature.Rotation, false)
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
	if r.goal == policy.EnsureCooking && r.definition == "Campfire" {
		// The cooking campfire stands outdoors or in a non-sleeping room,
		// never beside a bed or spot (#1179).
		protected = append(append([]domain.Cell(nil), protected...), sleepingRoomCells(facts.Rooms)...)
	}
	var cells []policy.SiteCell
	adjacent := map[domain.Cell]bool{}
	for _, c := range r.adjacent {
		adjacent[c] = true
	}
	roomCells := map[domain.Cell]bool{}
	var interiorRooms []policy.InteriorRoom
	restricted := r.temperature != nil || r.facility != nil || r.cells != nil
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
		interiorRooms = policy.InteriorRoomsFor(*r.facility, rooms, facts.Cells)
		if len(roomCells) == 0 {
			if clockDebug() {
				var summary []string
				for _, room := range rooms.Rooms {
					summary = append(summary, fmt.Sprintf("%s role=%v enclosed=%v cells=%d", room.ID, room.Role, room.Enclosed, len(room.Cells)))
				}
				clockSchedulerLog("%s: no room hosts the facility %+v; rooms=%v", r.goal, *r.facility, summary)
			}
			return nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
		}
	}
	if r.cells != nil {
		allowed := map[domain.Cell]bool{}
		for _, c := range r.cells {
			allowed[c] = true
			if r.facility == nil {
				// No facility: the cells are the room (the kitchen, #835).
				roomCells[c] = true
			}
		}
		for c := range roomCells {
			if !allowed[c] {
				delete(roomCells, c)
			}
		}
		if len(roomCells) == 0 {
			return nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
		}
		// A layout room can stand past the colony-centred planning window
		// (#838): read the room's own cells rather than find no site.
		if source := observation.PlanningWindowFrom(call); source != nil && !windowHolds(facts.Cells, roomCells) {
			held, err := source.PlanningWindow(call, boundary.Identity(snapshot), cellsBox(roomCells))
			if err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			if !held.Complete {
				return nil, policy.StockObservation{}, BuildingMethodUnknown, nil
			}
			facts.Cells = held.Value.Cells
		}
	}
	for _, c := range facts.Cells {
		if r.recreationPowerW > 0 && !poweredRecreationCell(facts, c.Cell, r.recreationPowerW) {
			continue
		}
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
	if r.facility != nil {
		// A facility furnishes the room nearest its district (#609); the
		// radius still reaches the colony centre so the starter shell stays
		// a candidate until a room stands in the district.
		anchor := layoutAnchor(facts, r.district())
		searchRequest.Center, searchRequest.Radius = anchor, 22+max(anchor.X-facts.Center.X, facts.Center.X-anchor.X, anchor.Z-facts.Center.Z, facts.Center.Z-anchor.Z)
	}
	if r.goal == policy.EnsureCooking && r.definition == "Campfire" {
		// The cooking campfire stands by the base, not the landing
		// centroid (#1534).
		anchor := planCore(facts)
		searchRequest.Center, searchRequest.Radius = anchor, 22+max(anchor.X-facts.Center.X, facts.Center.X-anchor.X, anchor.Z-facts.Center.Z, facts.Center.Z-anchor.Z)
	}
	if r.cells != nil {
		// The room is fixed: search around it, wherever it stands (#838).
		box := cellsBox(roomCells)
		searchRequest.Center = domain.Cell{X: box.X + box.Width/2, Z: box.Z + box.Height/2}
		searchRequest.Radius = max(box.Width, box.Height)/2 + 1
	}
	if r.power != nil {
		searchRequest.Center, searchRequest.Radius = r.power.Center, 6
		if r.definition == policy.WindTurbineDefinition {
			// A turbine wants open ground its 7x16 catch zone can lie on,
			// which the cells right beside a consumer rarely offer.
			searchRequest.Radius = 12
		}
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
	// A workshop bench has an interaction spot on one side, so a small room
	// often rejects the north-facing anchor ("interaction spot is blocked by
	// wooden wall") while another facing fits; every other definition keeps
	// the single native-default orientation.
	rotations := []domain.Rotation{domain.North}
	if r.workshop != nil {
		rotations = []domain.Rotation{domain.North, domain.East, domain.South, domain.West}
	}
	var pending *placementChoice
	commit := func() error {
		selected = append(selected, pending.choice)
		if err := mergeRoutineStock(&stock, pending.preview.Stock, len(selected) == 1); err != nil {
			return err
		}
		footprint, _ := pending.choice.Footprint.Value()
		for _, c := range footprint {
			usedCells[c] = true
		}
		pending = nil
		return nil
	}
	// previewAt previews the definition at one anchor and rotation; false
	// when the native preview or the search refuses the site.
	previewAt := func(search policy.PlacementSearch, id string, c domain.Cell, rotation domain.Rotation) (placementChoice, bool, RoutineBuildingReason, error) {
		if err := check(); err != nil {
			return placementChoice{}, false, "", err
		}
		b, err := domain.NewBuilding(r.definition, c, rotation, r.stuff)
		if err != nil {
			return placementChoice{}, false, "", err
		}
		a, err := domain.NewBuildingAction(domain.ActionID(id), b)
		if err != nil {
			return placementChoice{}, false, "", err
		}
		preview, _, err := r.native.PreviewBuilding(call, a, snapshot)
		if err != nil {
			return placementChoice{}, false, "", err
		}
		made, known := preview.Preview.MadeFromStuff.Value()
		if (r.phase == policy.ComfortRanked || r.phase == policy.ComfortBasic) && (r.definition == "HorseshoesPin" || r.definition == "TubeTelevision") {
			accessible, known := preview.Preview.WatchCellsAccessible.Value()
			unknownWatch = unknownWatch || !known
			if !known || !accessible {
				return placementChoice{}, false, "", nil
			}
		}
		if !known || made != (r.stuff != "") {
			return placementChoice{}, false, BuildingMethodUnknown, nil
		}
		if r.definition == policy.WindTurbineDefinition {
			// Only a site whose native catch zone is clear makes the
			// turbine's nominal output; an obstructed one is no site.
			if blocked, known := preview.Preview.WindBlockedCells.Value(); !known || blocked > 0 {
				return placementChoice{}, false, "", nil
			}
		}
		choice, score, ok, err := search.SelectScored(r.definition, r.stuff, []policy.Preview{preview.Preview})
		if err != nil || !ok {
			return placementChoice{}, false, "", err
		}
		return placementChoice{choice: choice, preview: preview, score: score}, true, "", nil
	}
	var occupied []domain.Cell
	if len(interiorRooms) > 0 {
		for _, c := range facts.Cells {
			if o, known := c.Occupied.Value(); !known || o {
				occupied = append(occupied, c.Cell)
			}
		}
	}
	// overlaps refuses a footprint on a site already chosen, or one that
	// cuts a planned room's door-to-door aisle or strands its floor (#801).
	overlaps := func(p policy.Preview) bool {
		footprint, _ := p.Footprint.Value()
		for _, c := range footprint {
			if usedCells[c] {
				return true
			}
		}
		for _, room := range interiorRooms {
			blocked := map[domain.Cell]bool{}
			for _, c := range occupied {
				blocked[c] = true
			}
			for c := range usedCells {
				blocked[c] = true
			}
			if !policy.InteriorPlacementWalkable(room, blocked, footprint) {
				return true
			}
		}
		return false
	}
	// pass runs one search. Candidates come nearest first, and a
	// footprint's score is its distance, so once the next candidate's distance reaches the best valid score no
	// later candidate can beat it: the best is committed and the previews
	// stop there.
	pass := func(search policy.PlacementSearch, tag string) (RoutineBuildingReason, error) {
		for i, c := range search.Candidates() {
			if int64(len(selected)) == missing {
				break
			}
			if pending != nil && search.Score(c, nil).Distance >= pending.score.Score {
				if err := commit(); err != nil {
					return "", err
				}
				if int64(len(selected)) == missing {
					break
				}
			}
			for _, rotation := range rotations {
				id := fmt.Sprintf("%s%s-%d", snapshot.Plan, tag, i)
				if rotation != domain.North {
					id = fmt.Sprintf("%s%s-%d-%s", snapshot.Plan, tag, i, rotation)
				}
				choice, ok, reason, err := previewAt(search, id, c, rotation)
				if err != nil || reason != "" {
					return reason, err
				}
				if !ok {
					continue
				}
				if !overlaps(choice.choice) && (pending == nil || choice.score.Score < pending.score.Score) {
					pending = &choice
				}
				break
			}
		}
		if pending != nil && int64(len(selected)) < missing {
			return "", commit()
		}
		return "", nil
	}
	// An indoor facility asks each room's interior template for the
	// piece's slots first (#800) and takes any slot that can be placed,
	// whatever its score. Only then does the scored search run, over the
	// rooms' wall bands, centre lines and mirror positions before any
	// other free cell.
	if len(interiorRooms) > 0 {
		var slots []policy.InteriorPiece
		var anchors []domain.Cell
		for _, room := range interiorRooms {
			anchors = append(anchors, policy.InteriorSnapAnchors(room, occupied)...)
			plan, ok := policy.PlanInterior(room, policy.InteriorPieceDefFor(r.definition))
			if !ok {
				continue
			}
			for _, p := range plan.Pieces {
				// The search drops occupied anchors, so a slot whose
				// anchor is taken cannot be previewed.
				if p.Accepts(r.definition) && !slices.Contains(occupied, p.Anchor()) {
					slots = append(slots, p)
				}
			}
		}
		if len(slots) > 0 {
			request := searchRequest
			request.Anchors, request.Limit = nil, min(len(slots), 64)
			for _, p := range slots {
				request.Anchors = append(request.Anchors, p.Anchor())
			}
			slotSearch, err := policy.NewPlacementSearch(request)
			if err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			for i, p := range slots {
				if int64(len(selected)) == missing {
					break
				}
				choice, ok, reason, err := previewAt(slotSearch, fmt.Sprintf("%s-t%d", snapshot.Plan, i), p.Anchor(), p.Rot)
				if err != nil || reason != "" {
					return nil, policy.StockObservation{}, reason, err
				}
				if ok && !overlaps(choice.choice) {
					clockSchedulerLog("%s: %s takes interior slot %s at %d,%d %s", r.goal, r.definition, p.Slot, p.Anchor().X, p.Anchor().Z, p.Rot)
					pending = &choice
					if err := commit(); err != nil {
						return nil, policy.StockObservation{}, "", err
					}
				}
			}
		}
		if int64(len(selected)) < missing {
			request := searchRequest
			request.Anchors = anchors
			snapSearch, err := policy.NewPlacementSearch(request)
			if err != nil {
				return nil, policy.StockObservation{}, "", err
			}
			if reason, err := pass(snapSearch, "-s"); err != nil || reason != "" {
				return nil, policy.StockObservation{}, reason, err
			}
		}
	}
	if int64(len(selected)) < missing {
		if reason, err := pass(search, ""); err != nil || reason != "" {
			return nil, policy.StockObservation{}, reason, err
		}
	}
	if int64(len(selected)) != missing {
		if unknownWatch {
			return nil, policy.StockObservation{}, BuildingMethodUnknown, nil
		}
		clockSchedulerLog("%s: no site for %s (%s): selected=%d missing=%d candidates=%d siteCells=%d roomCells=%d restricted=%v environment=%s", r.goal, r.definition, r.stuff, len(selected), missing, len(search.Candidates()), len(cells), len(roomCells), restricted, searchRequest.Environment)
		if clockDebug() && restricted {
			blocked := map[domain.Cell]bool{}
			for _, c := range searchRequest.Protected {
				blocked[c] = true
			}
			var rows []string
			for _, c := range cells {
				occupied, _ := c.Occupied.Value()
				zone, zoneKnown := c.Zone.Value()
				indoors, _ := c.Indoors.Value()
				rows = append(rows, fmt.Sprintf("%d,%d w=%v o=%v z=%v/%v i=%v p=%v", c.Cell.X, c.Cell.Z, c.Walkable, occupied, zone, zoneKnown, indoors, blocked[c.Cell]))
			}
			clockSchedulerLog("%s: site census %v", r.goal, rows)
		}
		return nil, policy.StockObservation{}, BuildingMethodNoSpace, nil
	}
	return selected, stock, "", nil
}

// routineScope is the observation scope a planner checks its review
// against: the bare tick read when the native offers one (bridge.Client
// does), which the step's bundle seeds into the read cache so no planner
// crosses the bridge for it (#593), else the identity read. Only the
// context and pause state are used; the capability list is not.
func routineScope(ctx context.Context, native observation.ColonySource) (observation.Identity, error) {
	if source, ok := native.(observation.Source); ok {
		reply, _, err := source.Tick(ctx)
		if err != nil {
			return observation.Identity{}, err
		}
		return observation.DecodeTick(reply)
	}
	reply, _, err := native.Identity(ctx)
	if err != nil {
		return observation.Identity{}, err
	}
	return observation.DecodeIdentity(reply)
}

// routineBuildingBoundary is the scope every routine planner checks its own
// identity read against: the reviewed world and native generation, at a
// tick that still describes the review's anchor (routineBuildingFresh).
func routineBuildingBoundary(actual observation.Identity, expected domain.GenerationSnapshot, tick domain.Tick) bool {
	generation, generationKnown := actual.NativeGeneration.Value()
	return actual.Colony == expected.Colony && actual.Load == expected.Load && actual.Map == expected.Map && routineBuildingFresh(actual.Tick, tick) && generationKnown && generation == expected.Native
}

// routineBuildingFresh accepts an identity read the step's fact cache may
// serve: seeded at the tick the step opened on, it sits behind the review's
// colony anchor under a running window and is not a changed world (#306,
// #662).
func routineBuildingFresh(actual, anchor domain.Tick) bool {
	return routineCachedFresh(bridge.FactIdentity, actual, anchor)
}

// routineCachedFresh accepts any observation the step's fact cache may
// have served: a read's tick never makes it stale, only another world does,
// and that is checked beside it (#306, #662).
func routineCachedFresh(family bridge.FactFamily, actual, anchor domain.Tick) bool {
	return true
}

// initialShelterOwed reports whether the review binds an active
// MaintainHousing goal still in deficit on its starter-shelter phase.
func initialShelterOwed(ctx context.Context, p *Player, review store.RoutineReview) (bool, error) {
	if review.Latches.Housing != policy.HousingShelter {
		return false, nil
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainHousing {
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

// sleepingBedsPerEpoch bounds the builds one epoch may stage under the same
// selection: beds for one owed count, campfires after burn-outs.
const sleepingBedsPerEpoch = 8

// nextSleepingBedMethod returns the method the next build takes. The
// selection names a method by the beds still owed, but that count need not
// fall after a staged bed is assigned (the colonist it went to may have been
// counted as housed, or another colonist's bed may have turned unsuitable),
// so a method whose plan is no longer open yields to a numbered successor;
// only a method whose plan is still open stays the one reported used. A
// building gone from the census (a campfire that burnt out, a bed that
// disappeared) and one never built both mean try again (#856).
func (r *RoutineBuildingPlanner) nextSleepingBedMethod(call context.Context, goal store.GoalState, method domain.MethodID, census domain.Fact[policy.CurrentConstruction]) (domain.MethodID, error) {
	p := r.reviewer.player
	base := method
	for n := 1; n < sleepingBedsPerEpoch; n++ {
		existing, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method)
		if errors.Is(err, store.ErrNotFound) {
			return method, nil
		}
		if err != nil {
			return "", err
		}
		plan, err := p.journal.LoadPlan(call, existing.Plan)
		if err != nil {
			return "", err
		}
		if store.PlanWorkOpen(plan, census) {
			return method, nil
		}
		method = domain.MethodID(fmt.Sprintf("%s-%d", base, n))
	}
	return method, nil
}

// placementChoice is a valid previewed footprint held while nearer-scored
// candidates are still possible.
type placementChoice struct {
	choice  policy.Preview
	preview bridge.BuildingPreview
	score   policy.PlacementScore
}

// previewResources are the distinct resources the previews' known costs
// name, sorted.
func previewResources(previews []policy.Preview) []policy.Resource {
	seen := map[policy.Resource]bool{}
	var out []policy.Resource
	for _, p := range previews {
		costs, _ := p.Costs.Value()
		for _, c := range costs {
			if c.Count > 0 && !seen[c.Resource] {
				seen[c.Resource] = true
				out = append(out, c.Resource)
			}
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// windowHolds is whether every room cell is a row of the planning window.
func windowHolds(cells []policy.SiteCell, room map[domain.Cell]bool) bool {
	listed := 0
	for _, c := range cells {
		if room[c.Cell] {
			listed++
		}
	}
	return listed == len(room)
}

// cellsBox is the smallest rectangle holding every cell; cells is non-empty.
func cellsBox(cells map[domain.Cell]bool) policy.Rectangle {
	first := true
	var minX, minZ, maxX, maxZ int32
	for c := range cells {
		if first {
			minX, minZ, maxX, maxZ, first = c.X, c.Z, c.X, c.Z, false
		}
		minX, minZ, maxX, maxZ = min(minX, c.X), min(minZ, c.Z), max(maxX, c.X), max(maxZ, c.Z)
	}
	return policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
}

// regularBedsShort reports whether the sleeping census holds fewer regular
// (humanlike, non-medical, non-prisoner) beds than max(colonists, housing
// target) plus the colony's slaves, whose beds count as regular: the
// shelter's spots were lost or converted, not merely uncounted.
func regularBedsShort(f policy.RoutineFacts) bool {
	sleeping, known := f.Sleeping.Value()
	count, ck := f.Colonists.Value()
	if !known || !ck {
		return false
	}
	if target, known := f.HousingTarget.Value(); known {
		count = max(count, target)
	}
	count += int64(len(sleeping.Slaves))
	regular := int64(0)
	for _, bed := range sleeping.Beds {
		humanlike, hk := bed.Humanlike.Value()
		medical, mk := bed.Medical.Value()
		prisoners, pk := bed.Prisoners.Value()
		if hk && mk && pk && humanlike && !medical && !prisoners {
			regular++
		}
	}
	return regular < count
}

// developmentSelects reports whether the development rows let goal build.
// A resource or equipment goal held on an existing commitment keeps
// building its prerequisite bench (#981).
func developmentSelects(rows []store.RoutineDevelopmentRow, goal domain.GoalID) bool {
	for _, row := range rows {
		held := row.Committed && (goal == policy.MaintainResource || goal == policy.MaintainEquipment)
		if row.Goal == goal && (row.Selected || held) {
			return true
		}
	}
	return false
}
