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

type RoundsBuildingSource interface {
	observation.ColonySource
	PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}

type RoundsBuildingResult struct {
	Verdict
	Decision store.BuildingMethodDecision
	// NativeWorkTicks bounds ordinary roofing or comfort use after observed construction.
	// It neither asserts recovery nor grants native authority.
	NativeWorkTicks uint32
}

// RoundsBuildingPlanner compiles one bounded building method under an
// existing reviewed player direction. It creates shared pending work, never
// acquires a lease, dispatches an action or advances the game.
type RoundsBuildingPlanner struct {
	paste            []policy.SiteBuilding
	reviewer         *Rounder
	native           RoundsBuildingSource
	concern          policy.ConcernID
	definition       string
	recreationPowerW float64
	stuff            string
	environment      policy.PlacementEnvironment
	adjacent         []domain.Cell
	shelter          bool
	excavation       RoundsExcavationSource
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

func NewRoundsSleepingPlanner(reviewer *Rounder, native RoundsBuildingSource) (*RoundsBuildingPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoundsSleepingPlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoundsBuildingPlanner{reviewer: reviewer, native: native, concern: policy.MaintainHousing, phase: policy.HousingShelter, definition: "SleepingSpot"}, nil
}

// step is also used by the scheduler already holding the same player gate.
func (r *RoundsBuildingPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoundsBuildingResult, error) {
	roofingOnly := false
	if r.shelter {
		indoor := *r
		indoor.shelter, indoor.definition = false, "SleepingSpot"
		if r.facilityLadder() {
			indoor.definition = ""
		}
		result, err := indoor.step(call, epoch, arbiter)
		clockSchedulerLog("%s: indoor step reason=%v err=%v", r.concern, result.Verdict, err)
		if err != nil || !result.Verdict.Is(RefusalNoSpace) && !result.Verdict.Is(WaitMethodUsed) && !result.Verdict.Is(WaitBunksOpen) {
			return result, err
		}
		roofingOnly = result.Verdict.Is(WaitMethodUsed)
	}
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBuildingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoundsBuildingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoundsBuildingResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.WorkableOwner(call, review, r.concern)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !workable {
		return RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	// A phased goal walks its phases in order: only the planner for the
	// phase the review left owed runs.
	if r.phase != "" && review.Latches.Phase(r.concern) != r.phase {
		return RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
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
			return RoundsBuildingResult{}, err
		}
		if blocked {
			return RoundsBuildingResult{Verdict: BuildingShellBlocked}, nil
		}
	}
	if r.phase == policy.ComfortBasic {
		owed, err := initialShelterOwed(call, p, review)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if owed {
			return RoundsBuildingResult{Verdict: BuildingShelterPending}, nil
		}
	}
	if r.phase == policy.ComfortRanked || r.phase == policy.HousingExpansion || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes || (r.concern == policy.MaintainResource || r.concern == policy.MaintainEquipment) || r.phase == policy.HousingSleeping {
		if !developmentSelects(review.Development.Rows, r.concern) {
			return RoundsBuildingResult{Verdict: awaitingSlot(string(r.concern))}, nil
		}
	}
	bunksOpen := false
	for _, m := range goal.OwnerMethods() {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if r.concern == policy.MaintainButcherSpot {
			// Only a pending spot of this definition is the planner's own work (#260).
			for _, progress := range plan.Progress {
				if pendingFacility(progress, r.definition) || pendingFacility(progress, "TableButcher") {
					return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
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
		if r.shelter && isRoomDigMethod(m.Method) {
			// Mining is separate work from the ring: prepareShell reads the
			// dig's own state and the ring goes up beside it.
			continue
		}
		if store.PlanOpen(plan) {
			return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
		}
	}
	if bunksOpen {
		return RoundsBuildingResult{Verdict: BuildingBunksOpen}, nil
	}
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) {
		// ErrControl alone reads as a lost writer gate in the diagnosis
		// (#662); name the boundary that actually failed.
		clockSchedulerLog("%s: ErrControl identity boundary observed=%+v tick=%d review tick=%d snapshot=%+v", r.concern, expected, expected.Tick, review.Tick, state.Snapshot)
		return RoundsBuildingResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	if r.concern == policy.MaintainResource || r.concern == policy.MaintainEquipment {
		var recorded func()
		call, recorded = recordPlannerStep(call, r.concern, state.Snapshot, review.Tick)
		defer recorded()
		selection, reason, err := r.prepareWorkshop(call, state, review)
		if err != nil || !reason.IsZero() {
			return RoundsBuildingResult{Verdict: reason}, err
		}
		prepared := *r
		prepared.workshop = selection
		r = &prepared
	}
	definitions := []string{r.definition}
	if r.concern == policy.EnsureCooking {
		definitions = []string{"Campfire", "NutrientPasteDispenser", "Hopper"}
	}
	var furniture policy.DiningFurniture
	if r.phase == policy.ComfortRanked && !r.shelter || r.phase == policy.ComfortBasic {
		var joy []string
		var err error
		if furniture, joy, err = recreationDefinitions(call, r.native, boundary.Identity(state.Snapshot)); err != nil {
			return RoundsBuildingResult{}, err
		}
		definitions = furniture.Definitions()
		if r.phase != policy.ComfortBasic {
			joy = nil
		}
		for _, name := range joy {
			if !slices.Contains(definitions, name) {
				definitions = append(definitions, name)
			}
		}
	}
	if (r.concern == policy.MaintainResource || r.concern == policy.MaintainEquipment) && !r.shelter {
		definitions = r.workshop.candidates
	}
	if (r.concern == policy.MaintainMedicalReserves || r.phase == policy.HousingSleeping || r.concern == policy.EnsureResearch) && !r.shelter {
		// The beds and the research bench are the furniture rules', read
		// with every catalog (observation.ColonyProjection.Shapes).
		definitions = nil
	}
	var pendingConsumers []string
	if r.concern == policy.EnsureBasicPower {
		// The consumers other goals' admitted plans are about to build join
		// the census request so the budget can price their draw.
		held, err := p.journal.BuildingReservations(call, state.Snapshot)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		pendingConsumers = pendingBuildingDefinitions(held)
		definitions = append(append([]string{"Wall", "Door"}, policy.PowerFamilyDefinitions()...), pendingConsumers...)
	}
	if r.concern == policy.EnsureTemperatureSafety {
		definitions = temperatureDefinitions
	}
	if r.concern == policy.MaintainRefrigeration {
		definitions = []string{"Cooler"}
	}
	if r.concern == policy.MaintainLighting {
		definitions = r.lightingDefinitions()
	}
	if r.concern == policy.MaintainFlooring {
		definitions = r.flooringDefinitions()
	}
	if r.concern == policy.MaintainRoutes {
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
	if r.concern == policy.EnsureCooking || r.concern == policy.MaintainRefrigeration {
		// The planned kitchen or freezer is shelled first (#835).
		observed = append(append([]string(nil), observed...), "Wall", "Door")
	}
	if r.shelter {
		if r.concern == policy.MaintainButcherSpot {
			observed = append(append([]string(nil), observed...), "TableButcher")
		}
		// The door ladder proposes an Autodoor only once the read shows it
		// available (#610); the ring never waits on it.
		observed = append(append([]string(nil), observed...), "Autodoor")
	}
	var reading observation.ColonyReading
	_, roundsSource := r.native.(observation.RoundsSource)
	separation := (r.concern == policy.MaintainButcherSpot || r.concern == policy.EnsureCooking) && roundsSource
	if r.concern == policy.EnsureTemperatureSafety || r.facilityLadder() || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes || separation {
		// Cooking and butcher placements read rooms too when the source can
		// serve them, so kitchen/butcher separation protects each other's
		// rooms (issue #6 slice 2); without a census nothing is protected.
		full, readErr := r.reviewer.observeRooms(call, r.native.(observation.RoundsSource), expected, domain.Unknown[[]policy.ConstructionClaim](), observed...)
		reading, err = full.ColonyReading, readErr
	} else if r.concern == policy.EnsureBasicPower || r.phase == policy.ComfortBasic {
		full, readErr := r.reviewer.observeOwned(call, r.native.(observation.RoundsSource), expected, domain.Unknown[[]policy.ConstructionClaim](), observed...)
		reading, err = full.ColonyReading, readErr
	} else {
		reading, err = r.reviewer.observeColony(call, r.native, expected, observed)
	}
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	facts := reading.Projection
	recordStepRead("building", r.concern, state.Snapshot, facts)
	if r.concern == policy.EnsureCooking {
		// A cooking campfire in a sleeping room, or one a stove kitchen
		// supersedes, is deconstructed first (#1179).
		claims, err := p.journal.ConstructionClaims(call, state.Snapshot, expected.Tick)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if known, ok := claims.Value(); ok {
			if campfire, owed := campfireRetirement(facts, known); owed {
				result, err := r.retireCampfire(call, epoch, state, review, goal, reading, campfire)
				if err != nil || !result.Verdict.Is(WaitMethodUsed) {
					return result, err
				}
			}
		}
		resolved, reason := r.selectPaste(facts)
		if !reason.IsZero() {
			return RoundsBuildingResult{Verdict: reason}, nil
		}
		r = resolved
	}
	var coolingAllowance uint32
	var coolingLent bool
	if r.facilityLadder() && !r.shelter || r.phase == policy.ComfortBasic || r.concern == policy.EnsureBasicPower || r.concern == policy.EnsureTemperatureSafety || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes {
		var resolved *RoundsBuildingPlanner
		var reason Verdict
		if r.concern == policy.EnsureTemperatureSafety {
			resolved, reason, err = r.selectTemperature(facts, review.Latches)
		} else if r.concern == policy.MaintainRefrigeration {
			var exhausted bool
			coolingAllowance, exhausted, coolingLent, err = refrigerationOutputAllowance(call, p.journal, goal, state.Snapshot, facts.Identity.Tick, review.Latches.RefrigerationSince)
			if err != nil {
				return RoundsBuildingResult{}, err
			}
			// The dining room's meal closet stands before its cooler
			// (#936); a shell already tried this epoch, or refused, leaves
			// the cooling to go on.
			if closet, owed := plannedMealCloset(facts); positiveFact(owed) {
				result, err := r.shellRoom(call, epoch, state, review, goal, reading, closet, plannedRoomMethod(closet), "cold meal shelf")
				if err != nil || !result.Verdict.skipsToPlacement() {
					return result, err
				}
			}
			resolved, reason, err = r.selectRefrigeration(call, facts, review.Latches, exhausted)
		} else if r.concern == policy.EnsureBasicPower {
			resolved, reason, err = r.selectPower(facts, pendingConsumers)
		} else if r.concern == policy.MaintainLighting {
			resolved, reason, err = r.selectLighting(facts, review.Latches)
		} else if r.concern == policy.MaintainFlooring {
			resolved, reason, err = r.selectFlooring(call, state.Snapshot, facts, review.Latches)
		} else if r.concern == policy.MaintainRoutes {
			resolved, reason, err = r.selectRoutes(facts, review.Latches)
		} else if r.concern == policy.MaintainResource || r.concern == policy.MaintainEquipment {
			resolved, reason, err = r.selectWorkshop(call, state, review, facts)
		} else if r.concern == policy.MaintainMedicalReserves {
			resolved, reason, err = r.selectHospital(facts)
		} else if r.concern == policy.EnsureResearch {
			resolved, reason, err = r.selectResearchBench(facts)
		} else if r.phase == policy.HousingSleeping {
			resolved, reason, err = r.selectSleeping(facts, review)
		} else if r.phase == policy.ComfortBasic {
			resolved, reason, err = r.selectBasicComfort(facts)
		} else {
			resolved, reason, err = r.selectComfort(facts, review.Comfort)
		}
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if !reason.IsZero() {
			result := RoundsBuildingResult{Verdict: reason}
			if r.concern == policy.EnsureBasicPower && reason.Is(RefusalNoSpace) {
				result.NativeWorkTicks, err = powerOutputAllowance(call, p.journal, goal, state.Snapshot, facts.Identity.Tick)
				return result, err
			}
			if r.concern == policy.EnsureBasicPower && reason == awaitingMethod(policy.PowerWaitFuel) {
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
			powerSettling := r.concern == policy.MaintainRefrigeration && reason == awaitingMethod(policy.RefrigerationPowerNeeded) && !coolingLent
			if reason == BuildingComfortWait || reason == awaitingMethod(policy.PowerWaitOutput) || reason == BuildingTemperatureWait || reason == awaitingMethod(policy.RefrigerationWait) || powerSettling {
				if r.concern == policy.EnsureTemperatureSafety {
					result.NativeWorkTicks, err = temperatureOutputAllowance(call, p.journal, goal, state.Snapshot, facts.Identity.Tick)
				} else if r.concern == policy.MaintainRefrigeration {
					result.NativeWorkTicks = coolingAllowance
				} else if r.concern == policy.EnsureBasicPower {
					result.NativeWorkTicks, err = powerOutputAllowance(call, p.journal, goal, state.Snapshot, facts.Identity.Tick)
				} else {
					result.NativeWorkTicks, err = comfortUseAllowance(call, p.journal, goal, state.Snapshot, facts.Identity.Tick, furniture)
				}
				if err != nil {
					return RoundsBuildingResult{}, err
				}
				if err := p.current(call, epoch); err != nil {
					return RoundsBuildingResult{}, err
				}
				if p.session.State() != state {
					return RoundsBuildingResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
				}
				last, _, err := r.native.Identity(call)
				if err != nil {
					return RoundsBuildingResult{}, err
				}
				actual, err := observation.DecodeIdentity(last)
				if err != nil || !roundsBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick) {
					return RoundsBuildingResult{}, fmt.Errorf("%w: step: err != nil || !roundsBuildingBoundary(actual, state.Snapshot, facts.Identity.Tick)", ErrControl)
				}
				now := r.reviewer.clock.Now()
				if now.Before(reading.StartedAt) || now.Sub(reading.StartedAt) > r.reviewer.maxAge {
					return RoundsBuildingResult{}, observation.ErrStale
				}
			}
			return result, nil
		}
		r = resolved
		definitions = []string{r.definition}
		furnish, result, done, err := r.plannedDiningFurnishing(call, epoch, state, review, goal, reading, facts)
		if done || err != nil {
			return result, err
		}
		r = furnish
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
		if err == nil && result.Verdict.Is(WaitMethodUsed) {
			result.NativeWorkTicks = coolingAllowance
		}
		return result, err
	}
	missing, method, reason := r.selection(facts)
	if method == "butcher-spot-retire" {
		if spot, owed := standingButcherSpot(facts); owed {
			return r.retireBuilding(call, epoch, state, review, goal, reading, spot, "butcher-spot-retire", "stand-in butcher spot")
		}
	}
	if method == "butcher-table" {
		// A stand-in spot stands apart: the real table goes in the same room (the
		// planned butchery) once the wood is in stock.
		table := *r
		table.definition = "TableButcher"
		table.stuff = facts.BuildStuff(table.definition)
		r = &table
	}
	if reason == BuildingExistingFacility {
		// The ring and the furniture are independent (#835): a campfire
		// already standing for the kitchen does not excuse the planned
		// room's ring.
		if module, ok := r.plannedRoomModule(); ok {
			if room, owed := plannedRoomOwed(facts, module); owed {
				result, err := r.shellRoom(call, epoch, state, review, goal, reading, room, plannedRoomMethod(room), "")
				if err != nil || result.Verdict.Is(WaitMethodUsed) {
					return result, err
				}
			}
		}
	}
	if !reason.IsZero() {
		return RoundsBuildingResult{Verdict: reason}, nil
	}
	// The stand-in campfire is free and instant and the colony cooks on it
	// while the kitchen is dug: it goes down outdoors now, and the ring is
	// shelled once it stands (the existing-facility path above).
	standIn := r.concern == policy.EnsureCooking && method == "campfire"
	if module, ok := r.plannedRoomModule(); ok && !standIn {
		// The planned room is raised and furnished together (#835): the
		// ring is admitted, and the stove or cooler goes onto the room's
		// interior without waiting for the walls. A shell already tried
		// this epoch, or refused, leaves the usual placement to go on.
		if room, owed := plannedRoomOwed(facts, module); owed {
			result, err := r.shellRoom(call, epoch, state, review, goal, reading, room, plannedRoomMethod(room), "")
			if err != nil || !result.Verdict.skipsToPlacement() {
				return result, err
			}
			if module == policy.ModuleKitchen || module == policy.ModuleButchery {
				kitchen := *r
				kitchen.cells, kitchen.environment = plannedRoomInterior(room), policy.PlacementAnywhere
				r = &kitchen
			}
		} else if module == policy.ModuleKitchen || module == policy.ModuleButchery {
			if cells := plannedRoomCells(facts, module); cells != nil {
				kitchen := *r
				kitchen.cells = cells
				r = &kitchen
			}
		}
	}
	if r.concern == policy.EnsureCooking {
		definitions = []string{r.definition}
		if len(r.paste) > 0 {
			definitions = append(definitions, "Hopper")
		}
	}
	gate := definitionsGate(facts, definitions, r.shelter)
	if len(r.paste) > 0 {
		gate = Verdict{}
		for _, name := range definitions {
			if gate = comfortBuilderGate(facts, name); !gate.IsZero() {
				break
			}
		}
	}
	if r.definition == "TableButcher" || r.facilityLadder() && !r.shelter || r.phase == policy.ComfortBasic || r.concern == policy.EnsureBasicPower || r.concern == policy.EnsureTemperatureSafety || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes {
		gate = comfortBuilderGate(facts, r.definition)
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
	if !gate.IsZero() {
		if clockDebug() {
			for _, d := range facts.Definitions {
				if d.Name == r.definition {
					clockSchedulerLog("%s: builder unavailable definition=%+v workPawns=%+v", goal.OwnerID(), d, facts.WorkPawns)
				}
			}
		}
		return RoundsBuildingResult{Verdict: gate}, nil
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
			return RoundsBuildingResult{}, err
		}
		if used != nil {
			if err := p.current(call, epoch); err != nil {
				return RoundsBuildingResult{}, err
			}
			if p.session.State() != state {
				return RoundsBuildingResult{}, fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
			}
			return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "shelter_method"), NativeWorkTicks: shelterNativeWorkTicks(*used, state.Snapshot, facts.Identity.Tick)}, nil
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
		if r.phase == policy.HousingSleeping || r.concern == policy.EnsureCooking || r.phase == policy.HousingShelter && regularBedsShort(facts.Facts) {
			if method, err = r.nextSleepingBedMethod(call, goal, method, facts.Facts.CurrentConstruction); err != nil {
				return RoundsBuildingResult{}, err
			}
		}
		if _, loadErr := p.journal.LoadOwnerMethod(call, goal, method); loadErr == nil {
			return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "sleeping_method")}, nil
		} else if !errors.Is(loadErr, store.ErrNotFound) {
			return RoundsBuildingResult{}, loadErr
		}
	}
	if roofingOnly {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "roofing_method")}, nil
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	held, err := p.journal.BuildingReservations(call, snapshot)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if !r.shelter && (r.concern == policy.MaintainButcherSpot || r.concern == policy.EnsureCooking || r.facilityLadder() || r.phase == policy.ComfortBasic || r.phase == policy.HousingExpansion || r.concern == policy.EnsureBasicPower || r.concern == policy.EnsureTemperatureSafety || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes) {
		pending := func(progress domain.Progress) bool {
			if r.concern == policy.EnsureTemperatureSafety {
				if r.temperature.Method == policy.TemperatureHeat {
					return pendingFacility(progress, "Campfire") || pendingFacility(progress, "Heater")
				}
				return pendingFacility(progress, "PassiveCooler") || pendingFacility(progress, "Cooler")
			}
			if r.concern == policy.EnsureBasicPower {
				for _, name := range policy.PowerFamilyDefinitions() {
					if pendingFacility(progress, name) {
						return true
					}
				}
				return pendingFacility(progress, "PowerConduit") || pendingFacility(progress, "HiddenConduit") || pendingFacility(progress, "WaterproofConduit")
			}
			if r.phase == policy.HousingExpansion || r.concern == policy.MaintainMedicalReserves || r.phase == policy.HousingSleeping {
				return pendingFacility(progress, "SleepingSpot") || pendingFacility(progress, "Bed") || pendingFacility(progress, "DoubleBed") || pendingFacility(progress, "RoyalBed")
			}
			return pendingFacility(progress, r.definition)
		}
		playerPlan, err := p.journal.LoadPlan(call, state.Snapshot.Plan)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		for _, progress := range playerPlan.Progress {
			if pending(progress) {
				return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
			}
		}
		for _, reservation := range held {
			if pending(reservation.Progress) {
				return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
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
	for _, m := range goal.OwnerMethods() {
		own = append(own, m.Plan)
	}
	pendingAnchors, err := p.journal.PendingBuildingAnchors(call, snapshot, own)
	if err != nil {
		return RoundsBuildingResult{}, err
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
		call, finish = snap.StartPlanner(call, r.concern)
		defer func() {
			if err := finish(snapshot, facts.Identity.Tick); err != nil {
				clockSchedulerLog("%s: planner snapshot not recorded: %v", r.concern, err)
			}
		}()
	}
	if r.shelter && r.phase != policy.HousingShelter {
		if result, handled, err := r.prepareShellRoom(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, protected, check); err != nil || handled {
			return result, err
		}
	}
	if r.refrigeration != nil && r.refrigeration.Method == policy.RefrigerationBuild {
		if result, handled, err := r.digExhaust(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, check); err != nil || handled {
			return result, err
		}
	}
	if result, handled, err := r.digSky(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, protected, check); err != nil || handled {
		return result, err
	}
	if r.power != nil && r.power.FixedSite() {
		if result, handled, err := r.digGeothermal(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, protected, check); err != nil || handled {
			return result, err
		}
	}
	if len(r.paste) > 0 {
		if result, handled, err := r.digPaste(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, protected, check); err != nil || handled {
			return result, err
		}
	}
	if r.shelter && r.phase == policy.HousingShelter {
		// The starter shell is raised around its bunks (#612): the sleeping
		// spots and then the beds are placed on the site first, each a rung
		// under this goal, and the ring follows once the beds stand.
		var handled *RoundsBuildingResult
		selected, stock, reason, handled, err = r.stepShelterSite(call, epoch, shelterSite{state: state, review: review, owner: goal, facts: facts, read: reading, snapshot: snapshot, protected: protected, check: check})
		if err != nil || handled != nil {
			if handled == nil {
				handled = &RoundsBuildingResult{}
			}
			return *handled, err
		}
	} else {
		selected, stock, reason, err = r.previewMethod(call, snapshot, facts, protected, missing, check)
		if err == nil && r.routes != nil && reason.Is(RefusalNoSpace) {
			if result, handled, digErr := r.digBreach(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, protected, check); digErr != nil || handled {
				return result, digErr
			}
		}
	}
	if err != nil || !reason.IsZero() {
		return RoundsBuildingResult{Verdict: reason}, err
	}
	// Admission never checks stock: RimWorld places the blueprints
	// regardless and the frames hold natively for materials, which
	// MaintainResource then reads as the deficit (#602).
	purpose := policy.Rounds
	if r.shelter || r.power != nil && r.power.Method == policy.PowerShelter {
		purpose = policy.Shelter
	}
	return r.admitPreviews(call, epoch, roundsAdmission{state: state, review: review, owner: goal, facts: facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: purpose})
}

// roundsAdmission is what admitPreviews commits: the previews a method
// selected under one observation, bound to the goal as that method.
type roundsAdmission struct {
	state  ControlState
	review store.Rounds
	owner  store.WorkOwner
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
func (r *RoundsBuildingPlanner) admitPreviews(call, epoch context.Context, a roundsAdmission) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	actions := make([]domain.Action, len(a.selected))
	// A shell goes out as one wave with its door first in dispatch order
	// (the preview lists it first). The walls are not gated on the door
	// completing: a door blueprint or frame no more seals a room than a
	// wall's does, and gating held every wall until the door stood, or for
	// ever when its observation came back unknown (#602). The pen shell in
	// rounds_animal_containment.go orders its ring the same way.
	for i, v := range a.selected {
		actions[i] = v.Action
	}
	plan, err := domain.NewPlan(a.snapshot.Plan, a.snapshot.Revision, actions)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	decision, err := admitMethod(call, p.journal, store.BuildingMethodRequest{Owner: a.owner, Method: a.method, Reason: a.reason, Plan: plan, Current: a.snapshot, Tick: a.facts.Identity.Tick, Bounds: domain.Known(a.facts.Bounds), Stock: a.stock, Previews: a.selected, Purpose: a.purpose})
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	reason := admissionRefused(decision)
	if decision.Admitted {
		reason = BuildingReasonAdmitted
		// A shell admitted short of a material (#602) records each
		// shortfall so the ranking orders that resource's acquisition ahead
		// of unrelated optional work (#651, #728). The review keeps an edge
		// only while its actions stay open.
		if a.purpose == policy.Shelter {
			for _, resource := range previewResources(a.selected) {
				if rec, short := store.ShortfallDependency(r.concern, decision.Standard.Standard, a.method, plan.ID(), a.selected, a.stock, resource, a.facts.Identity.Tick); short {
					if err = p.journal.RecordDependency(call, a.review.Revision, rec); err != nil && !errors.Is(err, store.ErrConflict) {
						return RoundsBuildingResult{}, err
					}
				}
			}
		}
	}
	return RoundsBuildingResult{Verdict: reason, Decision: decision}, nil
}

// stockWaitTicks bounds one clock window lent to a method refused for
// insufficient stock. The census counts what lies on the map, and a stack a
// pawn is carrying to a stockpile, a bench is about to finish or a hauler is
// about to unforbid is invisible to it; without ticks the haul never lands
// and the refusal repeats until the clock parks on no_work. The next step
// re-reads the census, so the wait is the window, not a belief about stock.
const stockWaitTicks = domain.TicksPerHour

func (r *RoundsBuildingPlanner) previewMethod(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
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
			if err != nil || !reason.Is(RefusalNoSpace) {
				break
			}
			next := *r
			next.definition = name
			selected, stock, reason, err = next.previewPowerOrSearch(call, snapshot, facts, protected, missing, check)
		}
	}
	return selected, stock, reason, err
}

func (r *RoundsBuildingPlanner) previewSearch(call context.Context, snapshot domain.GenerationSnapshot, facts observation.ColonyProjection, protected []domain.Cell, missing int64, check func() error) ([]policy.Preview, policy.StockObservation, Verdict, error) {
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
	if (r.concern == policy.MaintainHousing || r.concern == policy.EnsureComfort && r.phase == policy.ComfortBasic && r.cells == nil) && r.facility == nil {
		// Loose sleeping spots and the basic-comfort table and seat go to the
		// starter shell or a bedroom, not into a standing kitchen, lab, storage
		// or other planned room: beds there make it a sleeping room and block the
		// room's own furnishing, and furniture there carves up its warehouse zone.
		protected = append(append([]domain.Cell(nil), protected...), nonSleepingPlannedCells(facts)...)
	}
	if r.definition == "ButcherSpot" || r.definition == "TableButcher" || r.concern == policy.EnsureCooking {
		protected = append(append([]domain.Cell(nil), protected...), policy.SeparationProtectedCells(facts.Rooms, r.definition == "ButcherSpot" || r.definition == "TableButcher")...)
	}
	if r.concern == policy.EnsureCooking && r.definition == "Campfire" {
		// The cooking campfire stands outdoors or in a non-sleeping room,
		// never beside a bed or spot (#1179).
		protected = append(append([]domain.Cell(nil), protected...), sleepingRoomCells(facts.Rooms)...)
		protected = append(protected, sleepingPlannedCells(facts)...)
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
			return nil, policy.StockObservation{}, fieldUnavailable("rooms"), nil
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
				clockSchedulerLog("%s: no room hosts the facility %+v; rooms=%v", r.concern, *r.facility, summary)
			}
			return nil, policy.StockObservation{}, noSpace("hosting_room"), nil
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
			return nil, policy.StockObservation{}, noSpace("layout_room_cells"), nil
		}
	}
	for _, c := range facts.Cells {
		if r.recreationPowerW > 0 && !poweredRecreationCell(facts, c.Cell, r.recreationPowerW) {
			continue
		}
		if restricted && !roomCells[c.Cell] {
			continue
		}
		if comfortFurniture(facts).IsChair(r.definition) && !adjacent[c.Cell] {
			continue
		}
		if roofed, known := c.Roofed.Value(); r.environment == policy.PlacementAnywhere || known && roofed {
			cells = append(cells, c)
		}
	}
	center, planned := facts.Center().Value()
	if !planned && r.cells == nil && r.power == nil {
		return nil, policy.StockObservation{}, BuildingNoLayoutPlan, nil
	}
	searchRequest := policy.PlacementSearchRequest{Snapshot: snapshot, Tick: facts.Identity.Tick, Bounds: facts.Bounds, Center: center, Cells: cells, Protected: append(append([]domain.Cell(nil), protected...), policy.DoorwayAisles(facts.Bounds, facts.Cells)...), Environment: policy.PlacementIndoors, Radius: max(facts.Bounds.Width, facts.Bounds.Height), Limit: 64}
	if r.facility != nil {
		// A facility furnishes the free room of its role nearest the colony
		// centre (#609); the search spans the map, so the
		// starter shell stays a candidate until such a room stands.
		anchor := center
		if module, ok := r.roomModule(); ok {
			if near, ok := roomAnchor(facts, module, center); ok {
				anchor = near
			}
		}
		searchRequest.Center = anchor
	}
	if r.concern == policy.EnsureCooking && r.definition == "Campfire" || r.definition == "ButcherSpot" || r.definition == "TableButcher" {
		// The cooking campfire and the butcher spot stand by the base, not the landing
		// centroid (#1534).
		if anchor, ok := planCore(facts); ok {
			searchRequest.Center = anchor
		}
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
		return nil, policy.StockObservation{}, Verdict{}, err
	}
	var selected []policy.Preview
	unknownWatch := false
	comfort, _ := facts.Facts.Comfort.Value()
	watchBuildings := comfort.WatchBuildings
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
		if err := mergeRoundsStock(&stock, pending.preview.Stock, len(selected) == 1); err != nil {
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
	previewAt := func(search policy.PlacementSearch, id string, c domain.Cell, rotation domain.Rotation) (placementChoice, bool, Verdict, error) {
		if err := check(); err != nil {
			return placementChoice{}, false, Verdict{}, err
		}
		b, err := domain.NewBuilding(r.definition, c, rotation, r.stuff)
		if err != nil {
			return placementChoice{}, false, Verdict{}, err
		}
		a, err := domain.NewBuildingAction(domain.ActionID(id), b)
		if err != nil {
			return placementChoice{}, false, Verdict{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, a, snapshot)
		if err != nil {
			return placementChoice{}, false, Verdict{}, err
		}
		made, known := preview.Preview.MadeFromStuff.Value()
		if (r.phase == policy.ComfortRanked || r.phase == policy.ComfortBasic) && slices.Contains(watchBuildings, r.definition) {
			accessible, known := preview.Preview.WatchCellsAccessible.Value()
			unknownWatch = unknownWatch || !known
			if !known || !accessible {
				return placementChoice{}, false, Verdict{}, nil
			}
		}
		if !known {
			return placementChoice{}, false, fieldUnavailable("made_from_stuff"), nil
		}
		if made != (r.stuff != "") {
			return placementChoice{}, false, siteBlocked(r.definition, "stuff_differs_from_native"), nil
		}
		if r.definition == policy.WindTurbineDefinition {
			// Only a site whose native catch zone is clear makes the
			// turbine's nominal output; an obstructed one is no site.
			if blocked, known := preview.Preview.WindBlockedCells.Value(); !known || blocked > 0 {
				return placementChoice{}, false, Verdict{}, nil
			}
		}
		choice, score, ok, err := search.SelectScored(r.definition, r.stuff, []policy.Preview{preview.Preview})
		if err != nil || !ok {
			return placementChoice{}, false, Verdict{}, err
		}
		return placementChoice{choice: choice, preview: preview, score: score}, true, Verdict{}, nil
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
	pass := func(search policy.PlacementSearch, tag string) (Verdict, error) {
		for i, c := range search.Candidates() {
			if int64(len(selected)) == missing {
				break
			}
			if pending != nil && search.Score(c, nil).Distance >= pending.score.Score {
				if err := commit(); err != nil {
					return Verdict{}, err
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
				if err != nil || !reason.IsZero() {
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
			return Verdict{}, commit()
		}
		return Verdict{}, nil
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
			// A definition with no buildable row has no shape or family: the
			// template plans its default layout for it.
			piece, _ := room.Piece(r.definition)
			piece.Def = r.definition
			plan, ok := policy.PlanInterior(room, piece)
			if !ok {
				continue
			}
			for _, p := range plan.Pieces {
				// The search drops occupied anchors, so a slot whose
				// anchor is taken cannot be previewed.
				if p.Accepts(room.Shapes, r.definition) && !slices.Contains(occupied, p.Anchor()) {
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
				return nil, policy.StockObservation{}, Verdict{}, err
			}
			// The search drops anchors that are protected, occupied or off the
			// site cells; previewing one anyway fails the whole step.
			proposed := map[domain.Cell]bool{}
			for _, c := range slotSearch.Candidates() {
				proposed[c] = true
			}
			for i, p := range slots {
				if int64(len(selected)) == missing {
					break
				}
				if !proposed[p.Anchor()] {
					continue
				}
				choice, ok, reason, err := previewAt(slotSearch, fmt.Sprintf("%s-t%d", snapshot.Plan, i), p.Anchor(), p.Rot)
				if err != nil || !reason.IsZero() {
					return nil, policy.StockObservation{}, reason, err
				}
				if ok && !overlaps(choice.choice) {
					clockSchedulerLog("%s: %s takes interior slot %s at %d,%d %s", r.concern, r.definition, p.Slot, p.Anchor().X, p.Anchor().Z, p.Rot)
					pending = &choice
					if err := commit(); err != nil {
						return nil, policy.StockObservation{}, Verdict{}, err
					}
				}
			}
		}
		if int64(len(selected)) < missing {
			request := searchRequest
			request.Anchors = anchors
			snapSearch, err := policy.NewPlacementSearch(request)
			if err != nil {
				return nil, policy.StockObservation{}, Verdict{}, err
			}
			if reason, err := pass(snapSearch, "-s"); err != nil || !reason.IsZero() {
				return nil, policy.StockObservation{}, reason, err
			}
		}
	}
	if int64(len(selected)) < missing {
		if reason, err := pass(search, ""); err != nil || !reason.IsZero() {
			return nil, policy.StockObservation{}, reason, err
		}
	}
	if int64(len(selected)) != missing {
		if unknownWatch {
			return nil, policy.StockObservation{}, fieldUnavailable("watch_cells_accessible"), nil
		}
		clockSchedulerLog("%s: no site for %s (%s): selected=%d missing=%d candidates=%d siteCells=%d roomCells=%d restricted=%v environment=%s", r.concern, r.definition, r.stuff, len(selected), missing, len(search.Candidates()), len(cells), len(roomCells), restricted, searchRequest.Environment)
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
			clockSchedulerLog("%s: site census %v", r.concern, rows)
		}
		return nil, policy.StockObservation{}, noSpace("placement_site"), nil
	}
	return selected, stock, Verdict{}, nil
}

// roundsBuildingBoundary is the scope every routine planner checks its own
// identity read against: the reviewed world and native generation, at a
// tick that still describes the review's anchor (roundsBuildingFresh).
func roundsBuildingBoundary(actual observation.Identity, expected domain.GenerationSnapshot, tick domain.Tick) bool {
	generation, generationKnown := actual.NativeGeneration.Value()
	return actual.Colony == expected.Colony && actual.Load == expected.Load && actual.Map == expected.Map && roundsBuildingFresh(actual.Tick, tick) && generationKnown && generation == expected.Native
}

// roundsBuildingFresh accepts an identity read the step's fact cache may
// serve: seeded at the tick the step opened on, it sits behind the review's
// colony anchor under a running window and is not a changed world (#306,
// #662).
func roundsBuildingFresh(actual, anchor domain.Tick) bool {
	return roundsCachedFresh(bridge.FactIdentity, actual, anchor)
}

// roundsCachedFresh accepts any observation the step's fact cache may
// have served: a read's tick never makes it stale, only another world does,
// and that is checked beside it (#306, #662).
func roundsCachedFresh(family bridge.FactFamily, actual, anchor domain.Tick) bool {
	return true
}

// initialShelterOwed reports whether the review binds an active
// MaintainHousing goal still in deficit on its starter-shelter phase.
func initialShelterOwed(ctx context.Context, p *Player, review store.Rounds) (bool, error) {
	if review.Latches.Housing != policy.HousingShelter {
		return false, nil
	}
	for _, binding := range review.Standards {
		if binding.Concern != policy.MaintainHousing {
			continue
		}
		goal, err := p.journal.LoadStandard(ctx, binding.Standard)
		if err != nil {
			return false, err
		}
		return goal.Standard.Status == domain.StandardOpen && goal.Standard.Finding == domain.FindingUnmet, nil
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
func (r *RoundsBuildingPlanner) nextSleepingBedMethod(call context.Context, goal store.WorkOwner, method domain.MethodID, census domain.Fact[policy.CurrentConstruction]) (domain.MethodID, error) {
	p := r.reviewer.player
	base := method
	for n := 1; n < sleepingBedsPerEpoch; n++ {
		existing, err := p.journal.LoadOwnerMethod(call, goal, method)
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
func regularBedsShort(f policy.RoundsFacts) bool {
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
func developmentSelects(rows []store.RoundsDevelopmentRow, goal domain.ConcernID) bool {
	for _, row := range rows {
		held := row.Committed && (goal == policy.MaintainResource || goal == policy.MaintainEquipment)
		if row.Concern == goal && (row.Selected || held) {
			return true
		}
	}
	return false
}

// nonSleepingPlannedCells are the interior cells of the layout plan's rooms
// built for another use than sleeping. Only the starter shell (the planned
// barracks) and unbuilt reserve ground stay open to loose spots and furniture.
func nonSleepingPlannedCells(facts observation.ColonyProjection) []domain.Cell {
	plan, ok := facts.LayoutPlan.Value()
	if !ok {
		return nil
	}
	var cells []domain.Cell
	for _, room := range plan.AllRooms() {
		switch room.Role {
		case policy.ModuleKitchen, policy.ModuleLab, policy.ModuleHospital, policy.ModulePrison, policy.ModuleDining, policy.ModuleRec, policy.ModuleFreezer, policy.ModuleWorkshop, policy.ModuleMealCloset, policy.ModuleButchery, policy.ModuleThrone, policy.ModuleNursery, policy.ModulePlayroom, policy.ModuleClassroom, policy.ModuleDeathrestChamber, policy.ModuleWorship, policy.ModuleContainmentCell, policy.ModuleIsolationRoom, policy.ModuleStorage, policy.ModuleArmory, policy.ModuleWardrobe:
		default:
			continue
		}
		cells = append(cells, plannedRoomInterior(room)...)
	}
	return cells
}

// sleepingPlannedCells are the interiors of the layout's bedrooms and
// barracks, built or not: the cooking campfire never goes where beds will
// stand. The census alone (sleepingRoomCells) protects a room only once a bed
// is in it, so the campfire went into rooms about to be bedrooms and was
// retired the moment the beds arrived, then placed again (#1179).
func sleepingPlannedCells(facts observation.ColonyProjection) []domain.Cell {
	plan, ok := facts.LayoutPlan.Value()
	if !ok {
		return nil
	}
	var cells []domain.Cell
	for _, room := range plan.AllRooms() {
		if room.Role == policy.ModuleBedroom || room.Role == policy.ModuleBarracks {
			cells = append(cells, plannedRoomInterior(room)...)
		}
	}
	return cells
}
