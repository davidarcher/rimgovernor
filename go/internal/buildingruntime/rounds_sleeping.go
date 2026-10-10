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
	paste []policy.SiteBuilding
	// slotCuts are the plants a refused shelter slot names on its footprint or
	// interaction cell; the step commits their cut wave and the slot is retried.
	slotCuts         []policy.ClearanceTarget
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
	// pave cells still natural ground with no floor ordered.
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
	// phase is the step of a phased goal (MaintainHousing, EnsureComfort) this
	// planner serves; the review's latched phase decides which one runs.
	// Empty for every other goal.
	phase policy.Phase
	// advancedLab marks the research step that builds the high-tech bench and
	// then its analyzer in the planned laboratory (never the shelter's
	// research slot, which holds only the simple bench): whichever of the two
	// is buildable and not yet standing is the one placed.
	advancedLab bool
	// partySpotRoom is the shared room the PartySpot step places in; empty
	// places in any roofed indoor cell.
	partySpotRoom string
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
		// The initial shelter and expansion furnish verified indoor space
		// first; only without any do they raise the planned shelter room.
		indoor := *r
		indoor.shelter, indoor.definition = false, "SleepingSpot"
		result, err := indoor.step(call, epoch, arbiter)
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
	if r.phase == policy.ComfortRanked && review.Latches.Phase(r.concern) == policy.ComfortSpot {
		// The PartySpot step is the ranked planner's third phase.
		spot := *r
		spot.phase, spot.definition, spot.environment = policy.ComfortSpot, policy.PartySpotDefinition, policy.PlacementIndoors
		r = &spot
	} else if r.phase != "" && review.Latches.Phase(r.concern) != r.phase {
		return RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
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
	bunksOpen := false
	for _, m := range goal.OwnerMethods() {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoundsBuildingResult{}, err
		}
		if r.concern == policy.MaintainButcherSpot {
			// Only a pending spot of this definition is the planner's own work.
			for _, progress := range plan.Progress {
				if pendingFacility(progress, r.definition) || pendingFacility(progress, "TableButcher") {
					return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, nil
				}
			}
			continue
		}
		if r.phase == policy.HousingShelter && isShelterBunkMethod(m.Method) {
			// An open bunk rung does not hold the ring: the walls and
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
		if !r.shelter && IsShellMethod(m.Method) {
			// A planned room's open ring wave does not hold the furniture
			// raised on its interior with it: the ring and the slot
			// are admitted together, and reconcileRoom leaves the ring alone
			// while its wave is open.
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
		// ErrControl alone reads as a lost writer gate in the diagnosis;
		// name the boundary that actually failed.
		return RoundsBuildingResult{}, fmt.Errorf("%w: step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick) observed tick=%d review tick=%d", ErrControl, expected.Tick, review.Tick)
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
	if r.phase == policy.ComfortRanked || r.phase == policy.ComfortBasic {
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
	if r.concern == policy.MaintainResource || r.concern == policy.MaintainEquipment {
		definitions = r.workshop.candidates
	}
	if r.concern == policy.MaintainMedicalReserves || r.phase == policy.HousingSleeping || r.concern == policy.EnsureResearch {
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
	// shelter also places its bunks before the ring, and their
	// availability is judged from the same read.
	observed := definitions
	if r.shelter && r.phase == policy.HousingShelter {
		observed = append(append([]string(nil), definitions...), "SleepingSpot", shelterBedDefinition, policy.SleepingBedrollDefinition)
	}
	if r.concern == policy.EnsureCooking || r.concern == policy.MaintainRefrigeration {
		// The planned kitchen or freezer is shelled first.
		observed = append(append([]string(nil), observed...), "Wall", "Door")
	}
	if r.shelter {
		// The door ladder proposes an Autodoor only once the read shows it
		// available; the ring never waits on it.
		observed = append(append([]string(nil), observed...), "Autodoor")
	}
	var reading observation.ColonyReading
	_, roundsSource := r.native.(observation.RoundsSource)
	separation := (r.concern == policy.MaintainButcherSpot || r.concern == policy.EnsureCooking) && roundsSource
	if r.phase == policy.ComfortSpot || r.concern == policy.EnsureTemperatureSafety || r.facilityLadder() || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes || separation {
		// Cooking and butcher placements read rooms too when the source can
		// serve them, so kitchen/butcher separation protects each other's
		// rooms; without a census nothing is protected.
		full, readErr := r.reviewer.observeRooms(call, r.native.(observation.RoundsSource), expected, domain.Unknown[[]policy.ConstructionClaim](), observed...)
		reading, err = full.ColonyReading, readErr
	} else if r.concern == policy.EnsureBasicPower || r.phase == policy.ComfortBasic || r.shelter {
		// The shelter ring reconciles against the construction census, which
		// only the rounds read carries.
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
	if r.phase == policy.ComfortSpot {
		result, placing, err := r.partySpotStep(call, epoch, state, review, goal, reading)
		if err != nil || placing == nil {
			return result, err
		}
		r = placing
	}
	if r.concern == policy.MaintainRoutes || r.concern == policy.EnsureTemperatureSafety {
		if result, handled, err := r.controlRoomDoor(call, epoch, state, goal, facts); handled || err != nil {
			return result, err
		}
	}
	if r.concern == policy.EnsureCooking {
		// A cooking campfire a stove kitchen supersedes is deconstructed
		// first.
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
	if r.facilityLadder() || r.phase == policy.ComfortBasic || r.concern == policy.EnsureBasicPower || r.concern == policy.EnsureTemperatureSafety || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes {
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
			// The dining room's meal closet stands before its cooler;
			// a shell already tried this epoch, or refused, leaves
			// the cooling to go on.
			if closet, owed := plannedMealCloset(facts); positiveFact(owed) {
				result, err := r.reconcileRoom(call, epoch, state, review, goal, observation.RoundsReading{ColonyReading: reading}, nil, roomReconcile{ringOnly: true, room: closet, name: string(plannedRoomMethod(closet)), reason: "cold meal shelf"})
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
			// cooler still unpowered when it runs out is a real hold.
			// Time lent from the latch alone does not cover it: with no
			// method in the epoch there is no cooler still settling.
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
		furnish, result, done, err := r.plannedFacilityFurnishing(call, epoch, state, review, goal, reading, facts)
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
	if method == "butcher-table" {
		// The real table goes in the planned butchery once the wood is in stock.
		table := *r
		table.definition = "TableButcher"
		table.stuff = facts.BuildStuff(table.definition)
		r = &table
	}
	if reason == BuildingExistingFacility {
		// The ring and the furniture are independent: a campfire
		// already standing for the kitchen does not excuse the planned
		// room's ring.
		if module, ok := r.plannedRoomModule(); ok {
			if room, owed := plannedRoomOwed(facts, module); owed {
				result, err := r.reconcileRoom(call, epoch, state, review, goal, observation.RoundsReading{ColonyReading: reading}, nil, roomReconcile{ringOnly: true, room: room, name: string(plannedRoomMethod(room))})
				if err != nil || result.Verdict.Is(WaitMethodUsed) {
					return result, err
				}
			}
		}
	}
	if !reason.IsZero() {
		return RoundsBuildingResult{Verdict: reason}, nil
	}
	// The shelter's template slots are keyed on its planned interior, so the
	// slot is previewed and admitted first and the ring wave goes in right
	// after it, around the placed slot: a ring blueprint on the
	// slot's interaction spot makes the native refuse the slot.
	var ringAfter *roomReconcile
	if r.takesShelterSlot(facts) {
		if room, owed := plannedRoomOwed(facts, policy.PlannedShelter); owed {
			ringAfter = &roomReconcile{ringOnly: true, room: room, name: string(plannedRoomMethod(room))}
		}
	}
	admitRing := func(goal store.WorkOwner) (RoundsBuildingResult, error) {
		return r.reconcileRoom(call, epoch, state, review, goal, observation.RoundsReading{ColonyReading: reading}, nil, *ringAfter)
	}
	if module, ok := r.plannedRoomModule(); ok {
		// The planned room is raised and furnished together: the
		// ring is admitted, and the stove or cooler goes onto the room's
		// interior without waiting for the walls. A shell already tried
		// this epoch, or refused, leaves the usual placement to go on.
		if room, owed := plannedRoomOwed(facts, module); owed {
			result, err := r.reconcileRoom(call, epoch, state, review, goal, observation.RoundsReading{ColonyReading: reading}, nil, roomReconcile{ringOnly: true, room: room, name: string(plannedRoomMethod(room))})
			if err != nil || !result.Verdict.skipsToPlacement() {
				return result, err
			}
			if r.hostsInPlannedRoom(module, facts) {
				kitchen := *r
				kitchen.cells, kitchen.environment = plannedRoomInterior(room), policy.PlacementAnywhere
				r = &kitchen
			}
		} else if r.hostsInPlannedRoom(module, facts) {
			if cells := plannedRoomCells(facts, module); cells != nil {
				kitchen := *r
				kitchen.cells = cells
				r = &kitchen
			}
		}
	}
	if r.concern == policy.EnsureCooking && r.definition == "Campfire" && r.cells == nil && len(r.paste) == 0 && !r.takesShelterSlot(facts) {
		// The cooking campfire goes in the planned kitchen once it stands.
		if cells := plannedRoomCells(facts, policy.PlannedKitchen); cells != nil {
			fire := *r
			fire.cells = cells
			r = &fire
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
	if r.definition == "TableButcher" || r.facilityLadder() || r.phase == policy.ComfortBasic || r.phase == policy.ComfortSpot || r.concern == policy.EnsureBasicPower || r.concern == policy.EnsureTemperatureSafety || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes {
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
		return RoundsBuildingResult{Verdict: gate}, nil
	}
	if r.shelter {
		check := func() error {
			if err := p.current(call, epoch); err != nil {
				return err
			}
			if p.session.State() != state {
				return fmt.Errorf("%w: step: p.session.State() != state", ErrControl)
			}
			return nil
		}
		return r.stepShelterRoom(call, epoch, shelterSite{state: state, review: review, owner: goal, facts: facts, read: reading, check: check}, roofingOnly)
	}
	// A campfire the pawns let burn out leaves the cooking census empty
	// again in the same epoch; the completed method yields to a numbered
	// successor the same way a staged bed's does.
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
		if ringAfter != nil {
			// The slot is placed already: the ring goes up around it.
			if result, err := admitRing(goal); err != nil || !result.Verdict.skipsToPlacement() {
				return result, err
			}
		}
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "sleeping_method")}, nil
	} else if !errors.Is(loadErr, store.ErrNotFound) {
		return RoundsBuildingResult{}, loadErr
	}
	planID := domain.MintPlanID()
	snapshot := state.Snapshot
	snapshot.Plan = planID
	snapshot.Revision = 1
	held, err := p.journal.BuildingReservations(call, snapshot)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if r.concern == policy.MaintainButcherSpot || r.concern == policy.EnsureCooking || r.facilityLadder() || r.phase == policy.ComfortBasic || r.phase == policy.ComfortSpot || r.phase == policy.HousingExpansion || r.concern == policy.EnsureBasicPower || r.concern == policy.EnsureTemperatureSafety || r.concern == policy.MaintainRefrigeration || r.concern == policy.MaintainLighting || r.concern == policy.MaintainFlooring || r.concern == policy.MaintainRoutes {
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
	// show nothing on the map; keep off their anchors.
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
	selected, stock, reason, err := r.previewMethod(call, snapshot, facts, protected, missing, check)
	if err == nil && r.routes != nil && reason.Is(RefusalNoSpace) {
		if result, handled, digErr := r.digBreach(call, epoch, excavationStep{state: state, review: review, owner: goal, facts: facts, read: reading}, protected, check); digErr != nil || handled {
			return result, digErr
		}
	}
	if err == nil && !reason.IsZero() && len(r.slotCuts) > 0 {
		// A plant on the only accepted slot is a foreign obstruction: cut it, and
		// the slot is retried once the order is done. A wave already
		// committed keeps the wait.
		if result, done, cutErr := r.commitObstructionWave(call, epoch, state, goal, string(r.concern)+"-slot", policy.OpCut, r.slotCuts); cutErr != nil || done {
			return result, cutErr
		}
	}
	if err != nil || !reason.IsZero() {
		return RoundsBuildingResult{Verdict: reason}, err
	}
	// Admission never checks stock: RimWorld places the blueprints
	// regardless and the frames hold natively for materials, which
	// MaintainResource then reads as the deficit.
	purpose := policy.Rounds
	if r.power != nil && r.power.Method == policy.PowerShelter {
		purpose = policy.Shelter
	}
	result, err := r.admitPreviews(call, epoch, roundsAdmission{state: state, review: review, owner: goal, facts: facts, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: purpose})
	if err != nil || ringAfter == nil || result.Verdict != BuildingReasonAdmitted {
		return result, err
	}
	// The slot is admitted: the shelter's ring wave goes in around it. The slot's
	// admission moved the owner on, so the ring commits against the fresh owner;
	// a ring that is not ready now is tried again next step.
	owner, workable, err := p.journal.WorkableOwner(call, review, r.concern)
	if err != nil || !workable {
		return result, err
	}
	if _, err := admitRing(owner); err != nil {
		return result, err
	}
	return result, nil
}

// roundsAdmission is what admitPreviews commits: the previews a method
// selected under one observation, bound to the goal as that method.
type roundsAdmission struct {
	state  ControlState
	review store.Rounds
	owner  store.WorkOwner
	facts  observation.ColonyProjection
	method domain.MethodID
	// reason is the planner's short why, stored with the method.
	reason   string
	snapshot domain.GenerationSnapshot
	selected []policy.Preview
	stock    policy.StockObservation
	purpose  policy.Purpose
}

// admitPreviews admits the plan, the previews in dispatch order. Native
// validates each building intent when it applies it, so the siting
// reads carry no boundary re-check here.
func (r *RoundsBuildingPlanner) admitPreviews(call, epoch context.Context, a roundsAdmission) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	actions := make([]domain.Action, len(a.selected))
	// A shell goes out as one wave with its door first in dispatch order
	// (the preview lists it first). The walls are not gated on the door
	// completing: a door blueprint or frame no more seals a room than a
	// wall's does, and gating held every wall until the door stood, or for
	// ever when its observation came back unknown. The pen shell in
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
		// A shell admitted short of a material records each
		// shortfall so the construction demand MaintainResource supplies
		// covers it. The review keeps a record only while its
		// actions stay open.
		// A Project owner (the planned kitchen's shell under EnsureCooking) has
		// no Standard: the record names Episodes, so it records none.
		if a.purpose == policy.Shelter && decision.Standard.Standard.ID != "" {
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
	// A loose sleeping spot stands only in a room planned to sleep in; the
	// basic-comfort table and seat keep out of the rooms that must stay clear.
	looseSpot := r.concern == policy.MaintainHousing && r.definition == "SleepingSpot" && r.facility == nil && r.cells == nil
	if !looseSpot && (r.concern == policy.MaintainHousing || r.concern == policy.EnsureComfort && (r.phase == policy.ComfortBasic || r.phase == policy.ComfortSpot) && r.cells == nil) && r.facility == nil {
		// Loose sleeping spots and the basic-comfort table and seat go to the
		// starter shell or a bedroom, not into a standing kitchen, lab, storage
		// or other planned room: beds there make it a sleeping room and block the
		// room's own furnishing, and furniture there carves up its warehouse zone.
		protected = append(append([]domain.Cell(nil), protected...), nonSleepingPlannedCells(facts)...)
	}
	if looseSpot {
		// A loose spot off the template's bed slots must not take the shelter's
		// campfire, cooler, crafting or bench slot: a cold map's cooking campfire
		// then finds its slot taken and falls through to the kitchen.
		protected = append(append([]domain.Cell(nil), protected...), shelterFurnitureCells(facts)...)
	}
	if r.definition == "TableButcher" || r.concern == policy.EnsureCooking {
		protected = append(append([]domain.Cell(nil), protected...), policy.SeparationProtectedCells(facts.Rooms, r.definition == "TableButcher")...)
	}
	// The cooking campfire stands in the planned kitchen; on a cold map
	// it takes the shelter's template slot. It has no outdoor stand-in.
	cookingCampfire := r.concern == policy.EnsureCooking && r.definition == "Campfire" && r.facility == nil && len(r.paste) == 0
	slotProtected := protected
	var cells []policy.SiteCell
	adjacent := map[domain.Cell]bool{}
	for _, c := range r.adjacent {
		adjacent[c] = true
	}
	roomCells := map[domain.Cell]bool{}
	var interiorRooms []policy.InteriorRoom
	restricted := r.temperature != nil || r.facility != nil || r.cells != nil
	if looseSpot {
		restricted = true
		for _, c := range sleepingPlannedCells(facts) {
			roomCells[c] = true
		}
	}
	// The crafting spot takes the standing shelter's template slot;
	// with no shelter, or its slot taken, it is placed as before.
	slotOnly := r.concern == policy.EnsureBasicDefense && r.definition == craftingSpotDefinition && r.facility == nil
	// On a cold map the campfires stand indoors on the shelter's template
	// slots; a taken slot, or no shelter, leaves the campfire to the
	// search below.
	if plan, known := facts.LayoutPlan.Value(); known && plan.Cold && r.definition == "Campfire" && (cookingCampfire || r.temperature != nil && r.temperature.Method == policy.TemperatureHeat) {
		slotOnly = len(plannedShelterRooms(facts)) > 0
	}
	// A hot map's passive cooler for the shelter stands on the template's
	// cooler slot; the powered wall cooler path is unchanged.
	if plan, known := facts.LayoutPlan.Value(); known && plan.Hot && r.definition == "PassiveCooler" && r.temperature != nil && r.temperature.Method == policy.TemperatureCool {
		for _, shelter := range plannedShelterRooms(facts) {
			for _, c := range r.temperature.Cells {
				in := shelter.Interior
				slotOnly = slotOnly || c.X >= in.X && c.X < in.X+in.Width && c.Z >= in.Z && c.Z < in.Z+in.Height
			}
		}
	}
	if cookingCampfire && r.cells == nil && !slotOnly {
		// Cooking waits for the planned kitchen.
		return nil, policy.StockObservation{}, BuildingNoLayoutPlan, nil
	}
	// The slots come from the planned interior, not a standing census room, so
	// they place before any wall does.
	plannedInterior := slotOnly
	if slotOnly {
		interiorRooms = plannedShelterRooms(facts)
	}
	// A campfire or cooler for a planned shelter has no outdoor stand-in: a slot
	// the native refuses waits, named by its blocker, instead of the search
	// below placing one outside the room. The crafting spot keeps its
	// placed-as-before fall-through.
	strictSlot := slotOnly && len(interiorRooms) > 0 && r.concern != policy.EnsureBasicDefense
	var refusedSlot *refusedPlacement
	var lastPreview policy.Preview
	if r.temperature != nil {
		for _, c := range r.temperature.Cells {
			roomCells[c] = true
		}
	}
	if r.facility != nil {
		if planned := plannedShelterRooms(facts); r.facility.Role == policy.RoomRoleLaboratory && !r.advancedLab && len(planned) > 0 {
			// The research bench takes the planned shelter's bench row.
			interiorRooms, plannedInterior = planned, true
			for _, room := range planned {
				for _, c := range rectCells(room.Interior) {
					roomCells[c] = true
				}
			}
		} else if r.cells != nil {
			// A bed's cells are the planned bedroom's; its interior
			// slots come from the plan room they lie in.
			for _, c := range r.cells {
				roomCells[c] = true
			}
			interiorRooms = plannedInteriorRooms(facts, func(planned policy.PlannedRoom) bool {
				return slices.ContainsFunc(r.cells, func(c domain.Cell) bool {
					in := planned.Interior
					return c.X >= in.X && c.X < in.X+in.Width && c.Z >= in.Z && c.Z < in.Z+in.Height
				})
			})
		}
		// Every other facility furnishes through its planned room, which sets
		// r.cells (plannedFacilityFurnishing); with none planned there
		// is no room to place in.
		if len(roomCells) == 0 {
			return nil, policy.StockObservation{}, noSpace("hosting_room"), nil
		}
	}
	if r.cells != nil {
		allowed := map[domain.Cell]bool{}
		for _, c := range r.cells {
			allowed[c] = true
			if r.facility == nil {
				// No facility: the cells are the room (the kitchen).
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
		// centre; the search spans the map, so the
		// starter shell stays a candidate until such a room stands.
		anchor := center
		if module, ok := r.roomModule(); ok {
			if near, ok := roomAnchor(facts, module, center); ok {
				anchor = near
			}
		}
		searchRequest.Center = anchor
	}
	if r.concern == policy.EnsureCooking && r.definition == "Campfire" || r.definition == "TableButcher" {
		// The cooking campfire and the butcher table stand by the base, not the landing
		// centroid.
		if anchor, ok := planCore(facts); ok {
			searchRequest.Center = anchor
		}
	}
	if r.cells != nil {
		// The room is fixed: search around it, wherever it stands.
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
		a, err := domain.NewBuildingAction(domain.ActionID(id), b, policy.PlannerTier(r.concern, r.phase))
		if err != nil {
			return placementChoice{}, false, Verdict{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, a, snapshot)
		if err != nil {
			return placementChoice{}, false, Verdict{}, err
		}
		lastPreview = preview.Preview
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
			if c.Occupied() {
				occupied = append(occupied, c.Cell)
			}
		}
	}
	// overlaps refuses a footprint on a site already chosen, or one that
	// cuts a planned room's door-to-door aisle or strands its floor.
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
	// piece's slots first and takes any slot that can be placed,
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
			request.Protected = append(append([]domain.Cell(nil), slotProtected...), policy.DoorwayAisles(facts.Bounds, facts.Cells)...)
			request.Anchors, request.Limit = nil, min(len(slots), 64)
			if plannedInterior {
				// The slots are keyed on the planned footprint, walled or not:
				// the interior's cells stand in whatever their roof.
				request.Environment, request.Cells = policy.PlacementAnywhere, plannedInteriorSiteCells(facts, interiorRooms)
			}
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
				if !ok && strictSlot && refusedSlot == nil {
					refused := reportRefused(call, roomBuild{def: r.definition, cell: p.Anchor()}, lastPreview)
					refusedSlot = &refused
					r.slotCuts = policy.SlotPlantCuts(facts.Cells, p, lastPreview.Blockers)
				}
				if ok && !overlaps(choice.choice) {
					pending = &choice
					if err := commit(); err != nil {
						return nil, policy.StockObservation{}, Verdict{}, err
					}
				}
			}
		}
		if int64(len(selected)) < missing && !slotOnly {
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
	if int64(len(selected)) < missing && strictSlot {
		subject := "shelter_slot:none_free"
		if refusedSlot != nil {
			subject = "shelter_slot:blocked:" + refusedSlot.key()
		}
		return nil, policy.StockObservation{}, waitFor(WaitExistingWork, subject), nil
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
// colony anchor under a running window and is not a changed world.
func roundsBuildingFresh(actual, anchor domain.Tick) bool {
	return roundsCachedFresh(bridge.FactIdentity, actual, anchor)
}

// roundsCachedFresh accepts any observation the step's fact cache may
// have served: a read's tick never makes it stale, only another world does,
// and that is checked beside it.
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
// disappeared) and one never built both mean try again.
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

// nonSleepingPlannedCells are the interior cells of the layout plan's rooms
// built for another use than sleeping. Only the shelter (the starter room) and unbuilt
// reserve ground stay open to loose spots and furniture.
func nonSleepingPlannedCells(facts observation.ColonyProjection) []domain.Cell {
	plan, ok := facts.LayoutPlan.Value()
	if !ok {
		return nil
	}
	var cells []domain.Cell
	for _, room := range plan.AllRooms() {
		switch room.Role {
		case policy.PlannedKitchen, policy.PlannedLab, policy.PlannedHospital, policy.PlannedPrison, policy.PlannedDining, policy.PlannedRec, policy.PlannedFreezer, policy.PlannedWorkshop, policy.PlannedMealCloset, policy.PlannedButchery, policy.PlannedThrone, policy.PlannedNursery, policy.PlannedPlayroom, policy.PlannedClassroom, policy.PlannedDeathrestChamber, policy.PlannedWorship, policy.PlannedContainmentCell, policy.PlannedIsolationRoom, policy.PlannedStorage, policy.PlannedArmory, policy.PlannedWardrobe, policy.PlannedBarn, policy.PlannedVetRoom:
		default:
			continue
		}
		cells = append(cells, plannedRoomInterior(room)...)
	}
	return cells
}

// shelterFurnitureCells are the cells of the planned shelter template's pieces
// that are not bunks: its campfire, cooler, crafting spot and research bench.
func shelterFurnitureCells(facts observation.ColonyProjection) []domain.Cell {
	var cells []domain.Cell
	for _, room := range plannedShelterRooms(facts) {
		full, ok := policy.PlanInterior(room, policy.InteriorPieceDef{})
		if !ok {
			continue
		}
		for _, p := range full.Pieces {
			if !p.IsBunk() {
				cells = append(cells, rectCells(p.Rect)...)
			}
		}
	}
	return cells
}

// sleepingPlannedCells are the interior cells of the layout plan's rooms a
// colonist sleeps in: the shelter, the bedrooms and the suites. Loose
// sleeping spots stand in these and nowhere else.
func sleepingPlannedCells(facts observation.ColonyProjection) []domain.Cell {
	plan, ok := facts.LayoutPlan.Value()
	if !ok {
		return nil
	}
	var cells []domain.Cell
	for _, room := range plan.AllRooms() {
		switch room.Role {
		case policy.PlannedShelter, policy.PlannedBedroom, policy.PlannedSuite:
			cells = append(cells, plannedRoomInterior(room)...)
		}
	}
	return cells
}
