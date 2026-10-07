package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type serviceClockReview struct {
	journal *store.Store
	profile string
}

func (s serviceClockReview) Read(ctx context.Context) (store.ClockReviewState, error) {
	return s.journal.ReadClockReview(ctx, s.profile)
}
func (s serviceClockReview) Acknowledge(ctx context.Context, ack store.ClockAcknowledgement) (store.ClockReviewState, error) {
	return s.journal.AcknowledgeClockEvents(ctx, s.profile, ack)
}

// serviceRoundsDiagnostics implements httpapi.RoundsProvider: a read-only,
// runtime-queryable view of which composed routine planner families this
// process wired up at startup and the durable review cursor's progress.
type serviceRoundsDiagnostics struct {
	journal        *store.Store
	reviewsEnabled bool
	methodsEnabled bool
	families       []string
	// sections is the state store the clock scheduler fills (#354); nil
	// without clock control.
	sections *facts.Store
}

func (s serviceRoundsDiagnostics) RoundsStatus(ctx context.Context) (httpapi.RoundsStatus, error) {
	review, err := s.journal.LoadRounds(ctx)
	if err != nil {
		return httpapi.RoundsStatus{}, err
	}
	status := httpapi.RoundsStatus{
		ReviewsEnabled:  s.reviewsEnabled,
		MethodsEnabled:  s.methodsEnabled,
		ActiveFamilies:  s.families,
		LastReviewTick:  review.Tick,
		LastReviewKnown: review.Revision != 0,
		Sections:        s.sections.Status(),
		LootHolds:       review.EventLoot.Held,
	}
	// Read-only diagnostics: missing complete extent geometry or readiness
	// stays unknown. This does not widen any planner or dispatch surface.
	if held, ok := facts.Get[observation.ColonyProjection](s.sections, facts.Colony); ok && held.Complete {
		f := held.Value.Facts
		r := policy.ResourceReachRequest{Bounds: domain.Known(held.Value.Bounds), RaidPoints: f.RaidPoints, Armed: f.Armed}
		if count, known := f.Hostiles.Value(); known {
			r.Threat = domain.Known(count > 0)
		}
		r.Extent, err = policy.DeriveColonyExtent(policy.ColonyExtentRequest{
			Bounds: r.Bounds, Construction: f.CurrentConstruction, Claims: f.ConstructionClaims,
			Home: f.HomeCoverage,
		})
		if err != nil {
			return httpapi.RoundsStatus{}, err
		}
		status.ResourceReach = r
		id := held.Value.Identity
		if id.Validate() == nil {
			history, err := s.journal.EstablishedColonyExtent(ctx, domain.GenerationSnapshot{Colony: id.Colony, Map: id.Map, Load: id.Load, Plan: "extent-diagnostics"}, id.Tick)
			if err != nil {
				return httpapi.RoundsStatus{}, err
			}
			extent := policy.ColonyExtent{Regions: []policy.ExtentRegion{}}
			for _, row := range history {
				extent.Regions = append(extent.Regions, row.Region)
			}
			status.ExtentEligibility = policy.ExtentEligibilityRequest{Extent: domain.Known(extent), Threat: r.Threat}
			buildings, bk := f.CurrentConstruction.Value()
			home, hk := f.HomeCoverage.Value()
			if bk && buildings.Colony && hk {
				ids := []string{}
				for _, b := range buildings.Buildings {
					ids = append(ids, b.ID)
				}
				// Every census stockpile (#719): a target with zone geometry.
				for _, t := range home.Targets {
					if g, known := t.ExtentGeometry.Value(); known && len(g.Zone) > 0 {
						ids = append(ids, t.ID)
					}
				}
				status.ExtentEligibility.Facilities = domain.Known(ids)
			}
			// No region route census is currently held: route_unknown is a hold.
		}
	}
	if review.Revision != 0 {
		development := review.Development.State()
		status.Development = &development
		status.Progress = review.Progress
		status.NoOps = review.NoOps
		status.Stage = review.Stage
		status.Roster = review.Roster
		status.ResourceRunways = review.ResourceRunwayState()
	}
	return status, nil
}

type serviceClockReads interface {
	buildingruntime.ClockWindowNative
	buildingruntime.ClockEventNative
}

// Window policy. A routine window runs defaultClockWindowTicks (one game
// day, the review guarantee of #126) unless danger or player input stops it
// earlier (#244, #584): the planners review and the Worker dispatches under
// the running window, so the stop between windows is the exception, not the
// review cadence. (The --clock-window-ticks override was removed in #875.)
//
// The default stays at a day. The review inputs that once reached the
// controller only through a review now have journal rows: the native
// digests cover research, faction relations, game conditions and zone
// edits (#626), a growing zone turning harvestable and a stock crossing a
// MaintainResource floor (#670). Raising the default is #669.
//
// A raid runs in combat windows that stop natively on the tick an armed
// combat event happens (#849); combatBackstopTicks bounds a window in which
// none does. 300 ticks is five game seconds at Normal: about one aimed
// shot cycle plus a few cells of a charge, so a fight that produced no
// event is still re-decided before it can drift far from the last plan,
// while a quiet siege does not stop more often than the planners take.
const (
	defaultClockWindowTicks = 60000
	combatBackstopTicks     = 300
	// maxClockBlindTicks is the wire bound on StartRequest.blind_tick_budget.
	maxClockBlindTicks = 1800000
)

func serviceClockConfig(profile string, testAcceleration bool, windowTicks, blindTicks uint32) buildingruntime.ClockSchedulerConfig {
	return buildingruntime.ClockSchedulerConfig{
		// MaxAge bounds how stale the admission reads (status, emergency)
		// may be by the time EvaluateClockWindow admits a window. The
		// planner facts are bound by tick instead (FactsTick), so the step
		// budget no longer constrains it; it stays at the step timeout so
		// a slow admission read under peer load still admits.
		Profile: profile, MaxAge: serviceClockStepTimeout,
		CombatMaxTicks: min(combatBackstopTicks, windowTicks),
		// Every window runs at Ultrafast whatever the native speed controls say;
		// serve --follow-player-speed opts back into the player's own speed
		// (#875); the dev tick boost makes every window boosted Ultrafast.
		FollowPlayerSpeed: false,
		Start: bridge.ClockStart{Speed: k.Speed_SPEED_ULTRAFAST, TestAcceleration: testAcceleration, PlayerAccelerated: !testAcceleration, LeaseMS: 30000, MaxTicks: windowTicks, BlindTickBudget: blindTicks,
			Policy: &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(),
				HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.5),
				HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}},
	}
}

// clockResourceThresholds is the operator's resource targets as the watch
// policy's stock levels, sorted by definition and cut at the wire bound.
func clockResourceThresholds(targets map[policy.Resource]int64) []*k.ResourceThreshold {
	names := make([]string, 0, len(targets))
	for name, level := range targets {
		if level >= 1 {
			names = append(names, string(name))
		}
	}
	slices.Sort(names)
	out := make([]*k.ResourceThreshold, 0, min(len(names), bridge.ClockResourceThresholdsMax))
	for _, name := range names[:min(len(names), bridge.ClockResourceThresholdsMax)] {
		out = append(out, &k.ResourceThreshold{DefName: proto.String(name), Level: proto.Int64(targets[policy.Resource(name)])})
	}
	return out
}

// serviceClockStepTimeout budgets one scheduler step: the rounds
// census plus every composed planner's native reads. It matches the Player's
// CallTimeout (serve_building.go) and is independent of the epoch lease. Under
// peer load (several headless RimWorld instances on one machine) a planner
// read alone can exceed the lease-bound 7s renew budget, which used to time
// out every step so no clock window was ever admitted (issue #73).
const serviceClockStepTimeout = time.Minute

// serviceClockTimeouts sizes the worker loops. Poll and renew share the
// bridge call timeout clamped under lease/4 (NewClockWorker's validation,
// with LeaseMS 30000 in serviceClockConfig): a renew call must never be late
// enough to let the native epoch lapse. The step has no lease constraint.
func serviceClockTimeouts(callTimeout time.Duration) serviceClockTimeoutConfig {
	lease := min(callTimeout, 7*time.Second)
	// The journal read is never held: the mod announces journal advances on
	// the rimgovernor.clock channel and the poll reads the page after each
	// (#2070). SignalWait only bounds how long a lost announcement waits;
	// the held read's #115 serialization stall cannot recur, so the
	// side-effect gate it needed is gone.
	return serviceClockTimeoutConfig{Poll: lease, Renew: lease, Step: serviceClockStepTimeout, SignalWait: serviceClockSignalWait, RunningPoll: 0}
}

// serviceClockSignalWait bounds the wait for a clock-channel announcement
// before the poll reads the journal anyway (the lost-announcement bound).
const serviceClockSignalWait = 4 * time.Second

type serviceClockTimeoutConfig struct{ Poll, Renew, Step, SignalWait, RunningPoll time.Duration }

// Session owns the attached worker's drain, including failed startup cleanup.
// Starting these loops does not enable Player or acquire native authority.
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, journal *store.Store, sc serveConfig, timeouts serviceClockTimeoutConfig, wake *buildingruntime.WakeSignal, sections *facts.Store, worldReady func(context.Context, *c.ObservationContext) (bool, error)) (*buildingruntime.ClockWorker, error) {
	profile, routine := sc.profile, sc.roundsEnabled
	sleeping, cooking, shelter, comfort, expansion, power, temperature := sc.roundsSleepingPlans, sc.roundsCookingPlans, sc.roundsShelterPlans, sc.roundsComfortPlans, sc.roundsExpansionPlans, sc.roundsPowerPlans, sc.roundsTemperaturePlans
	workshop := sc.workshopPlans()
	research := sc.researchPlans()
	hospital := sc.roundsHospitalPlans
	supplies, work, acquisition, defense, tend, rescue, equip := sc.roundsSupplyPlans, sc.roundsWorkPlans, sc.roundsAcquisitionPlans, sc.roundsDefensePlans, sc.roundsTendPlans, sc.roundsRescuePlans, sc.roundsEquipPlans
	repair, clean, gear, medical, foodStorageUpkeep := sc.roundsRepairPlans, sc.roundsCleanPlans, sc.roundsGearPlans, sc.roundsMedicalPlans, sc.roundsFoodStorageUpkeepPlans
	refrigeration := sc.roundsRefrigerationPlans
	fireSafety := sc.roundsFireSafetyPlans
	lighting := sc.roundsLightingPlans
	flooring := sc.roundsFlooringPlans
	routes := sc.roundsRoutesPlans
	animalContainment, recovery, husbandry, homeCoverage := sc.roundsAnimalContainmentPlans, sc.roundsRecoveryPlans, sc.roundsHusbandryPlans, sc.roundsHomeCoveragePlans
	resourceTargets := sc.resourceTargetsConfigured()
	animalFeedPlans := sc.roundsAnimalFeedPlans
	fields, bills := sc.roundsFieldPlans, sc.roundsBillPlans
	prisonerInteraction, populationCustody, stoneShell, defensiveLayout := sc.roundsPrisonerInteractionPlans, sc.roundsPopulationCustodyPlans, sc.roundsStoneShellPlans, sc.roundsDefensiveLayoutPlans
	incineration, moodRelief, dialog, trade := sc.roundsIncinerationPlans, sc.roundsMoodPlans, sc.roundsDialogPlans, sc.roundsTradePlans
	blight, pollution, mechCharger, geneBank := sc.roundsBlightPlans, sc.roundsPollutionPlans, sc.roundsMechChargerPlans, sc.roundsGeneBankPlans
	armory := sc.roundsArmoryPlans
	clearance := sc.roundsClearancePlans
	shrine := sc.roundsShrinePlans
	stockpiles, burial := sc.roundsStockpilePlans, sc.roundsBurialPlans
	config := serviceClockConfig(profile, sc.clockTestAcceleration, defaultClockWindowTicks, uint32(sc.clockBlindTicks))
	config.FollowPlayerSpeed = sc.followPlayerSpeed && !sc.clockTestAcceleration
	config.PaceHorizonTicks = domain.Tick(sc.clockBlindTicks)
	if sc.resourceTargetsConfigured() {
		// The native digest appends a colony row when a stock crosses one
		// of these levels (#670), so MaintainResource reviews under the
		// running window instead of waiting for the budget stop.
		config.Start.Policy.ResourceThresholds = clockResourceThresholds(sc.resourceTargets())
	}
	config.Store = sections
	config.Worker = true
	config.WorldReady = worldReady
	// Acceptance fault injection (#633): a failing or hanging planner, a
	// dropped renewal. Off unless the environment names one.
	faults, err := buildingruntime.ParseFaults(os.Getenv(buildingruntime.FaultsEnv))
	if err != nil {
		return nil, err
	}
	if err := faults.Validate(); err != nil {
		return nil, err
	}
	if !faults.Empty() {
		fmt.Fprintf(os.Stderr, "clock: fault injection active: %s\n", faults)
	}
	config.Faults = faults
	config.RoundsMethods = session.RoundsMethodsEnabled()
	if (bills || fields || acquisition || work || supplies || sleeping || cooking || shelter || comfort || hospital || expansion || power || temperature || defense || tend || rescue || equip || repair || fireSafety || clean || incineration || blight || pollution || mechCharger || geneBank || armory || clearance || shrine || moodRelief || gear || medical || foodStorageUpkeep || refrigeration || lighting || sc.roundsArtPlans || sc.roundsMechPlans || flooring || routes || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || sc.roundsPopulationJoinerPlans || homeCoverage || sc.roundsShelteringPlans || stoneShell || stockpiles || defensiveLayout || dialog || trade || resourceTargets || animalFeedPlans || sc.roundsRulesPlans) && !routine {
		return nil, errors.New("building plans require rounds")
	}
	if routine {
		native, ok := reads.(observation.RoundsSource)
		if !ok {
			return nil, errors.New("rounds require typed colony and emergency observations")
		}
		thresholds, capabilities := roundsCapabilities(sc)
		// The undraft sweep releases drafts no live plan needs (#939).
		if client, ok := reads.(*bridge.Client); ok {
			if capabilities.Undraft, err = bridge.NewActionsWriter(client); err != nil {
				return nil, err
			}
		}
		reviewer, err := buildingruntime.NewRounder(player, native, wallClock{}, thresholds, config.MaxAge, capabilities)
		if err != nil {
			return nil, err
		}
		config.Rounds = reviewer
		if bills {
			nativeBills, ok := reads.(buildingruntime.BillPlannerNative)
			if !ok {
				return nil, errors.New("bill plans require typed preview")
			}
			config.CookingBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.CookFood)
			if err != nil {
				return nil, err
			}
			config.BabyFoodBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.BabyFoodBill)
			if err != nil {
				return nil, err
			}
			config.PreservationBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.PreserveFood)
			if err != nil {
				return nil, err
			}
			config.ButcherBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.ButcherFood)
			if err != nil {
				return nil, err
			}
			buildingNative, ok := reads.(buildingruntime.RoundsBuildingSource)
			if !ok {
				return nil, errors.New("bill prerequisites require building observations")
			}
			config.Butcher, err = buildingruntime.NewRoundsButcherPlanner(reviewer, buildingNative)
			if err != nil {
				return nil, err
			}
		}
		if sc.roundsArtPlans {
			// MaintainArt's pinned sculpture bills (#1190) are their own
			// family, apart from the food bills.
			nativeBills, ok := reads.(buildingruntime.BillPlannerNative)
			if !ok {
				return nil, errors.New("art bills require typed preview")
			}
			config.ArtBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.ArtBill)
			if err != nil {
				return nil, err
			}
		}
		if sc.roundsMechPlans {
			// MaintainMechs' gestation bills (#1686) are their own bill family.
			nativeBills, ok := reads.(buildingruntime.BillPlannerNative)
			if !ok {
				return nil, errors.New("mech bills require typed preview")
			}
			config.MechBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.MechGestationBill)
			if err != nil {
				return nil, err
			}
		}
		if fields {
			fieldNative, ok := reads.(buildingruntime.FieldNative)
			if !ok {
				return nil, errors.New("field planning requires typed preview")
			}
			config.Fields, err = buildingruntime.NewRoundsFieldPlanner(reviewer, fieldNative)
			if err != nil {
				return nil, err
			}
		}
		if acquisition {
			config.FoodAcquisition, err = buildingruntime.NewRoundsAcquisitionPlanner(reviewer, policy.EnsureFoodSupply)
			if err != nil {
				return nil, err
			}
			config.PestAcquisition, err = buildingruntime.NewRoundsAcquisitionPlanner(reviewer, policy.ClearPests)
			if err != nil {
				return nil, err
			}
			config.ResourceAcquisition, err = buildingruntime.NewRoundsAcquisitionPlanner(reviewer, policy.MaintainResource)
			if err != nil {
				return nil, err
			}
		}
		if work {
			config.Work, err = buildingruntime.NewRoundsWorkPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if defense {
			defenseNative, ok := reads.(buildingruntime.RoundsDefenseSource)
			if !ok {
				return nil, errors.New("defense plans require typed combat observations")
			}
			config.Defense, err = buildingruntime.NewRoundsDefensePlanner(reviewer, defenseNative)
			if err != nil {
				return nil, err
			}
		}
		if tend {
			tendNative, ok := reads.(buildingruntime.RoundsTendSource)
			if !ok {
				return nil, errors.New("tend plans require typed tend observations")
			}
			config.Tend, err = buildingruntime.NewRoundsTendPlanner(reviewer, tendNative)
			if err != nil {
				return nil, err
			}
		}
		if rescue {
			rescueNative, ok := reads.(buildingruntime.RoundsRescueSource)
			if !ok {
				return nil, errors.New("rescue plans require typed combat observations")
			}
			config.Rescue, err = buildingruntime.NewRoundsRescuePlanner(reviewer, rescueNative)
			if err != nil {
				return nil, err
			}
		}
		if equip {
			equipNative, ok := reads.(buildingruntime.RoundsEquipSource)
			if !ok {
				return nil, errors.New("equip plans require typed equip observations")
			}
			config.Equip, err = buildingruntime.NewRoundsEquipPlanner(reviewer, equipNative)
			if err != nil {
				return nil, err
			}
		}
		// Shelves (#721) serve the stockpiles the methods create.
		if stockpiles {
			if shelvesNative, ok := reads.(buildingruntime.RoundsStorageShelvesSource); ok {
				if config.StorageShelves, err = buildingruntime.NewRoundsStorageShelvesPlanner(reviewer, shelvesNative); err != nil {
					return nil, err
				}
			}
		}
		if fireSafety {
			fireNative, ok := reads.(buildingruntime.RoundsFireSafetySource)
			if !ok {
				return nil, errors.New("fire safety plans require typed colony and tend observations")
			}
			config.FireSafety, err = buildingruntime.NewRoundsFireSafetyPlanner(reviewer, fireNative)
			if err != nil {
				return nil, err
			}
		}
		if repair {
			repairNative, ok := reads.(buildingruntime.RoundsRepairSource)
			if !ok {
				return nil, errors.New("repair plans require typed colony and tend observations")
			}
			config.Repair, err = buildingruntime.NewRoundsRepairPlanner(reviewer, repairNative)
			if err != nil {
				return nil, err
			}
		}
		if clean {
			cleanNative, ok := reads.(buildingruntime.RoundsCleanSource)
			if !ok {
				return nil, errors.New("clean plans require typed colony and tend observations")
			}
			config.Clean, err = buildingruntime.NewRoundsCleanPlanner(reviewer, cleanNative)
			if err != nil {
				return nil, err
			}
		}
		if clearance {
			clearanceNative, ok := reads.(buildingruntime.RoundsClearanceSource)
			if !ok {
				return nil, errors.New("clearance plans require typed colony observations")
			}
			config.Clearance, err = buildingruntime.NewRoundsClearancePlanner(reviewer, clearanceNative)
			if err != nil {
				return nil, err
			}
		}
		if shrine {
			shrineNative, ok := reads.(buildingruntime.RoundsShrineSource)
			if !ok {
				return nil, errors.New("shrine plans require typed shrine observations")
			}
			config.Shrine, err = buildingruntime.NewRoundsShrinePlanner(reviewer, shrineNative)
			if err != nil {
				return nil, err
			}
		}
		if armory {
			armoryNative, ok := reads.(buildingruntime.RoundsGearSource)
			if !ok {
				return nil, errors.New("armory plans require typed colony observations")
			}
			config.Armory, err = buildingruntime.NewRoundsArmoryPlanner(reviewer, armoryNative)
			if err != nil {
				return nil, err
			}
		}
		if blight {
			blightNative, ok := reads.(buildingruntime.RoundsBlightSource)
			if !ok {
				return nil, errors.New("blight plans require typed colony observations")
			}
			config.Blight, err = buildingruntime.NewRoundsBlightPlanner(reviewer, blightNative)
			if err != nil {
				return nil, err
			}
		}
		if burial {
			if config.Burial, err = buildingruntime.NewRoundsBurialPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if incineration {
			incinerationNative, ok := reads.(buildingruntime.RoundsIncinerationSource)
			if !ok {
				return nil, errors.New("incineration plans require typed colony and tend observations")
			}
			config.Incineration, err = buildingruntime.NewRoundsIncinerationPlanner(reviewer, incinerationNative)
			if err != nil {
				return nil, err
			}
		}
		if moodRelief {
			config.MoodRelief, err = buildingruntime.NewRoundsMoodReliefPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if gear {
			gearNative, ok := reads.(buildingruntime.RoundsGearSource)
			if !ok {
				return nil, errors.New("gear plans require typed colony observations")
			}
			config.Gear, err = buildingruntime.NewRoundsGearPlanner(reviewer, gearNative)
			if err != nil {
				return nil, err
			}
		}
		if medical {
			medicalNative, ok := reads.(buildingruntime.RoundsMedicalSource)
			if !ok {
				return nil, errors.New("medical reserve plans require typed colony observations")
			}
			config.Medical, err = buildingruntime.NewRoundsMedicalPlanner(reviewer, medicalNative)
			if err != nil {
				return nil, err
			}
			config.Surgery, err = buildingruntime.NewRoundsSurgeryPlanner(reviewer)
			if err != nil {
				return nil, err
			}
			// Parts a restore lacks are fabricated where researched (#1168).
			if nativeBills, ok := reads.(buildingruntime.BillPlannerNative); ok {
				config.SurgeryPartBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.SurgeryPartBill)
				if err != nil {
					return nil, err
				}
			}
		}
		if foodStorageUpkeep {
			foodStorageNative, ok := reads.(buildingruntime.RoundsFoodStorageUpkeepSource)
			if !ok {
				return nil, errors.New("food storage upkeep plans require typed colony and resource-source observations")
			}
			config.FoodStorageUpkeep, err = buildingruntime.NewRoundsFoodStorageUpkeepPlanner(reviewer, foodStorageNative)
			if err != nil {
				return nil, err
			}
		}
		if animalContainment {
			containmentNative, ok := reads.(buildingruntime.RoundsBuildingSource)
			if !ok {
				return nil, errors.New("animal containment plans require typed placement previews")
			}
			config.AnimalContainment, err = buildingruntime.NewRoundsAnimalContainmentPlanner(reviewer, containmentNative)
			if err != nil {
				return nil, err
			}
		}
		if recovery {
			config.Recovery, err = buildingruntime.NewRoundsRecoveryPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if husbandry {
			config.Husbandry, err = buildingruntime.NewRoundsHusbandryPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if sc.roundsRulesPlans {
			config.Rules, err = buildingruntime.NewRoundsRulesPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if prisonerInteraction {
			config.PrisonerInteraction, err = buildingruntime.NewRoundsPrisonerInteractionPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if sc.roundsPopulationJoinerPlans {
			config.PopulationJoiner, err = buildingruntime.NewRoundsPopulationJoinerPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if populationCustody {
			custodyNative, ok := reads.(buildingruntime.RoundsCustodySource)
			if !ok {
				return nil, errors.New("population custody plans require typed combat observations")
			}
			config.PopulationCustody, err = buildingruntime.NewRoundsPopulationCustodyPlanner(reviewer, custodyNative)
			if err != nil {
				return nil, err
			}
		}
		if sc.roundsFirebreakPlans {
			firebreakNative, ok := reads.(buildingruntime.RoundsFirebreakSource)
			if !ok {
				return nil, errors.New("firebreak plans require typed defense site observations")
			}
			if config.Firebreak, err = buildingruntime.NewRoundsFirebreakPlanner(reviewer, firebreakNative); err != nil {
				return nil, err
			}
		}
		if sc.roundsPsylinkPlans {
			psylinkNative, ok := reads.(buildingruntime.RoundsPsylinkSource)
			if !ok {
				return nil, errors.New("psylink plans require the typed neuroformer item read")
			}
			if config.Psylink, err = buildingruntime.NewRoundsPsylinkPlanner(reviewer, psylinkNative); err != nil {
				return nil, err
			}
		}
		if sc.roundsPermitPlans {
			// MaintainPermits (#1606) plans from the review's royalty read.
			if config.Permits, err = buildingruntime.NewRoundsPermitsPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if sc.roundsIdeoRolePlans {
			// MaintainIdeoRoles (#1661) plans from the review's ideology section and pawn rows.
			if config.IdeoRoles, err = buildingruntime.NewRoundsIdeoRolesPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if sc.roundsRitualPlans {
			// MaintainRituals (#1660) plans from the review's ideology section, pawn rows, building table and emergency census.
			if config.Rituals, err = buildingruntime.NewRoundsRitualsPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if sc.roundsCreepJoinerPlans {
			// ManageCreepJoiners (#1740) plans from the review's frame; no read of its own.
			if config.CreepJoiners, err = buildingruntime.NewRoundsCreepJoinerPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if sc.roundsShelteringPlans {
			// MaintainShelter (#1325) plans from the review's rooms; no read of its own.
			if config.MaintainShelter, err = buildingruntime.NewMaintainShelterPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if pollution {
			config.Pollution, err = buildingruntime.NewRoundsPollutionPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if homeCoverage {
			config.HomeCoverage, err = buildingruntime.NewRoundsHomeCoveragePlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if stoneShell {
			stoneShellNative, ok := reads.(buildingruntime.RoundsStoneShellSource)
			if !ok {
				return nil, errors.New("stone shell plans require typed wall upgrade site and placement observations")
			}
			config.StoneShell, err = buildingruntime.NewRoundsStoneShellPlanner(reviewer, stoneShellNative)
			if err != nil {
				return nil, err
			}
		}
		if stockpiles {
			stockpileNative, ok := reads.(buildingruntime.RoundsStockpileSource)
			if !ok {
				return nil, errors.New("stockpile plans require typed zone target observations")
			}
			config.Stockpiles, err = buildingruntime.NewRoundsStockpilePlanner(reviewer, stockpileNative)
			if err != nil {
				return nil, err
			}
		}
		if defensiveLayout {
			defenseNative, ok := reads.(buildingruntime.RoundsDefenseLayoutSource)
			if !ok {
				return nil, errors.New("defensive layout plans require typed defense site, lines of fire, spatial access, combat pawn and placement observations")
			}
			config.DefenseLayout, err = buildingruntime.NewRoundsDefenseLayoutPlanner(reviewer, defenseNative)
			if err != nil {
				return nil, err
			}
		}
		if research {
			researchNative, ok := reads.(buildingruntime.RoundsResearchSource)
			if !ok {
				return nil, errors.New("research plans require typed research observations")
			}
			config.Research, err = buildingruntime.NewRoundsResearchPlanner(reviewer, researchNative)
			if err != nil {
				return nil, err
			}
		}
		if dialog {
			dialogNative, ok := reads.(buildingruntime.RoundsDialogSource)
			if !ok {
				return nil, errors.New("dialog plans require typed colony observations")
			}
			config.Dialog, err = buildingruntime.NewRoundsDialogPlanner(reviewer, dialogNative, policy.DialogAnswerPolicy{Prefer: policy.DefaultDialogAnswerPrefer})
			if err != nil {
				return nil, err
			}
		}
		if trade {
			tradeNative, ok := reads.(buildingruntime.RoundsTradeSource)
			if !ok {
				return nil, errors.New("trade plans require typed trader and trade sheet observations")
			}
			config.Trade, err = buildingruntime.NewRoundsTradePlanner(reviewer, tradeNative)
			if err != nil {
				return nil, err
			}
		}
		if resourceTargets {
			resourceNative, ok := reads.(buildingruntime.RoundsResourceSource)
			if !ok {
				return nil, errors.New("resource plans require typed colony observations")
			}
			config.Resource, err = buildingruntime.NewRoundsResourcePlanner(reviewer, resourceNative)
			if err != nil {
				return nil, err
			}
		}
		if animalFeedPlans {
			animalFeedNative, ok := reads.(buildingruntime.RoundsResourceSource)
			if !ok {
				return nil, errors.New("animal feed plans require typed colony observations")
			}
			config.AnimalFeed, err = buildingruntime.NewRoundsAnimalFeedPlanner(reviewer, animalFeedNative)
			if err != nil {
				return nil, err
			}
		}
		if supplies {
			source, ok := reads.(buildingruntime.RoundsSupplySource)
			if !ok {
				return nil, errors.New("supply plans require typed supply observations")
			}
			config.Supplies, err = buildingruntime.NewRoundsSupplyPlanner(reviewer, source)
			if err != nil {
				return nil, err
			}
		}
		if sleeping || cooking || shelter || comfort || workshop || hospital || expansion || power || temperature || refrigeration || lighting || flooring || routes || mechCharger || geneBank {
			source, ok := reads.(buildingruntime.RoundsBuildingSource)
			if !ok {
				return nil, errors.New("building plans require typed placement previews")
			}
			if shelter {
				config.Sleeping, err = buildingruntime.NewRoundsShelterPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			} else if sleeping {
				config.Sleeping, err = buildingruntime.NewRoundsSleepingPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if sleeping {
				config.SleepingUpkeep, err = buildingruntime.NewRoundsSleepingUpkeepPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if temperature {
				config.Temperature, err = buildingruntime.NewRoundsTemperaturePlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if power {
				config.Power, err = buildingruntime.NewRoundsPowerPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if refrigeration {
				config.Refrigeration, err = buildingruntime.NewRoundsRefrigerationPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
				// The solar-flare cook-ahead bill is the refrigeration
				// goal's own method (#408), wired with its family rather than
				// the cooking bills so a refrigeration-only service serves it.
				nativeBills, ok := reads.(buildingruntime.BillPlannerNative)
				if !ok {
					return nil, errors.New("cook-ahead bills require typed preview")
				}
				config.CookAheadBills, err = buildingruntime.NewRoundsBillPlanner(reviewer, nativeBills, policy.CookAheadFood)
				if err != nil {
					return nil, err
				}
			}
			if mechCharger {
				config.MechCharger, err = buildingruntime.NewRoundsMechChargerPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if geneBank {
				config.GeneBank, err = buildingruntime.NewRoundsGeneBankPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if lighting {
				config.Lighting, err = buildingruntime.NewRoundsLightingPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if flooring {
				config.Flooring, err = buildingruntime.NewRoundsFlooringPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if routes {
				config.Routes, err = buildingruntime.NewRoundsRoutesPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if cooking || bills {
				config.Cooking, err = buildingruntime.NewRoundsCookingPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if expansion {
				config.Expansion, err = buildingruntime.NewRoundsExpansionPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if comfort {
				config.Comfort, err = buildingruntime.NewRoundsComfortPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
				config.BasicComfort, err = buildingruntime.NewRoundsBasicComfortPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if workshop {
				config.Workshop, err = buildingruntime.NewRoundsWorkshopPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if hospital {
				config.Hospital, err = buildingruntime.NewRoundsHospitalPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
		}
	}
	if config.Flooring != nil && config.Firebreak != nil {
		// MaintainFlooring paves the settled ring cells the firebreak
		// review found (#1549).
		config.Flooring.SetFirebreakPave(config.Firebreak.Pave)
	}
	scheduler, err := buildingruntime.NewClockScheduler(player, session, reads, config, wallClock{})
	if err != nil {
		return nil, err
	}
	return buildingruntime.NewClockWorker(ctx, scheduler, reads, buildingruntime.ClockWorkerConfig{
		PollInterval: time.Second, RenewInterval: 5 * time.Second, StepInterval: time.Second,
		MaxBackoff: 10 * time.Second, PollTimeout: timeouts.Poll, RenewTimeout: timeouts.Renew, StepTimeout: timeouts.Step, PageLimit: 128,
		SignalWait: timeouts.SignalWait, RunningPollInterval: timeouts.RunningPoll, Wake: wake,
	})
}

// roundsCapabilities derives the routine policy thresholds and the method
// capabilities a composed serve declares from its enabled families. Every
// family whose planner acts only while the development ranking selected its
// goal must declare that goal here, or the review ranks it method_unavailable
// and the planner never runs.
func roundsCapabilities(sc serveConfig) (policy.RoundsPolicy, buildingruntime.RoundsCapabilities) {
	thresholds := policy.DefaultRoundsPolicy()
	if !sc.footholdComposed() {
		// Foothold's exit criteria (shelter, cooking, food storage, basic
		// defense) have no planner in this composition, so the measured
		// stage could never climb and every staged family composed here
		// would never be raised: its goals, planners and the clock's work
		// all wait on a stage nothing can reach. Apply every stage's goals.
		thresholds.Stage.Floor = policy.StageDevelopment
	}
	capabilities := buildingruntime.RoundsCapabilities{LayoutOverlay: sc.layoutOverlay}
	if sc.roundsAcquisitionPlans || sc.roundsFieldPlans || sc.roundsBillPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureFoodSupply)
	}
	if sc.roundsAcquisitionPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainResource, policy.ClearPests)
	}
	if sc.roundsBillPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureCooking, policy.MaintainButcherSpot, policy.MaintainBabyFeeding)
	}
	if sc.roundsBillPlans || sc.roundsFoodStorageUpkeepPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFoodStorage)
	}
	if sc.roundsTemperaturePlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureTemperatureSafety)
	}
	if sc.roundsPowerPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureBasicPower)
	}
	if sc.roundsRefrigerationPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainRefrigeration)
	}
	if sc.roundsLightingPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainLighting)
	}
	if sc.roundsArtPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainArt)
	}
	if sc.roundsMechPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainMechs)
	}
	if sc.roundsFlooringPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFlooring)
	}
	if sc.roundsRoutesPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainRoutes)
	}
	if sc.roundsComfortPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureComfort)
	}
	if sc.roundsSleepingPlans || sc.roundsExpansionPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHousing)
	}
	if sc.roundsAnimalContainmentPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainAnimalContainment)
	}
	if sc.roundsRepairPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainEssentialRepairs)
	}
	if sc.roundsFireSafetyPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFireSafety)
	}
	if sc.roundsCleanPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainCleanFacilities)
	}
	if sc.roundsBurialPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainBurial)
	}
	if sc.roundsIncinerationPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainIncineration)
	}
	if sc.roundsGearPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainEquipment)
	}
	if sc.roundsClearancePlans {
		capabilities.Methods = append(capabilities.Methods, policy.ClearHomeObstructions)
	}
	if sc.roundsShrinePlans {
		capabilities.Methods = append(capabilities.Methods, policy.ClearAncientShrine)
	}
	if sc.roundsBlightPlans {
		capabilities.Methods = append(capabilities.Methods, policy.RemoveBlight)
	}
	if sc.roundsRecoveryPlans {
		capabilities.Methods = append(capabilities.Methods, policy.RecoverDisasterServices)
	}
	if sc.roundsHusbandryPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHerd)
	}
	if sc.roundsPrisonerInteractionPlans || sc.roundsPopulationCustodyPlans || sc.roundsPopulationJoinerPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainPopulation)
	}
	if sc.roundsPollutionPlans {
		capabilities.Methods = append(capabilities.Methods, policy.ManagePollution)
	}
	if sc.roundsMechChargerPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureMechCharger)
	}
	if sc.roundsGeneBankPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainGeneBank)
	}
	if sc.roundsHomeCoveragePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHomeCoverage)
	}
	if sc.roundsShelteringPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainShelter)
	}
	if sc.roundsFirebreakPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFirebreak)
	}
	if sc.roundsPsylinkPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainPsylink)
	}
	if sc.roundsCreepJoinerPlans {
		capabilities.Methods = append(capabilities.Methods, policy.ManageCreepJoiners)
	}
	if sc.roundsPermitPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainPermits)
	}
	if sc.roundsIdeoRolePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainIdeoRoles)
	}
	if sc.roundsRitualPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainRituals)
	}
	if sc.roundsStoneShellPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainStoneShell)
	}
	if sc.roundsStockpilePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainStockpiles)
	}
	if sc.roundsDefensiveLayoutPlans {
		thresholds.DefensiveLayout = true
		capabilities.Methods = append(capabilities.Methods, policy.EnsureDefensiveLayout)
	}
	thresholds.ResearchLadder = nil
	if sc.researchPlans() {
		thresholds.ResearchLadder = policy.DefaultResearchLadder()
		capabilities.Methods = append(capabilities.Methods, policy.EnsureResearch)
	}
	if sc.resourceTargetsConfigured() {
		thresholds.ResourceTargets = sc.resourceTargets()
		thresholds.StoneBlockTarget = policy.DefaultStoneBlockTarget
		// Acquisition already declares it (the wood floor).
		if !slices.Contains(capabilities.Methods, policy.MaintainResource) {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainResource)
		}
	} else {
		// No resource family: no floors, so trade and the workshop do not
		// chase targets nothing produces.
		thresholds.ResourceTargets, thresholds.StoneBlockTarget = nil, 0
	}
	if sc.roundsAnimalFeedPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainAnimalFeed)
	}
	if sc.roundsMedicalPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainMedicalReserves, policy.MaintainSurgery)
	}
	if sc.roundsTradePlans {
		thresholds.Trade = policy.RoundsTradePolicy{ComponentTarget: policy.DefaultResourceTargets()[policy.ComponentResource]}
		capabilities.Methods = append(capabilities.Methods, policy.TradeWithCaravan)
	}
	// The equip planner is EnsureBasicDefense's method: without this
	// declaration the priority-3 goal reviews as method_unavailable, never
	// wins a development slot, and every equip commit is refused (colony-2
	// ended with every survivor unarmed beside loose bows).
	if sc.roundsEquipPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureBasicDefense)
	}
	return thresholds, capabilities
}
