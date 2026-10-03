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

// serviceRoutineDiagnostics implements httpapi.RoutineProvider: a read-only,
// runtime-queryable view of which composed routine planner families this
// process wired up at startup and the durable review cursor's progress.
type serviceRoutineDiagnostics struct {
	journal        *store.Store
	reviewsEnabled bool
	methodsEnabled bool
	families       []string
	// sections is the state store the clock scheduler fills (#354); nil
	// without clock control.
	sections *facts.Store
}

func (s serviceRoutineDiagnostics) RoutineStatus(ctx context.Context) (httpapi.RoutineStatus, error) {
	review, err := s.journal.LoadRoutineReview(ctx)
	if err != nil {
		return httpapi.RoutineStatus{}, err
	}
	status := httpapi.RoutineStatus{
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
			return httpapi.RoutineStatus{}, err
		}
		status.ResourceReach = r
		id := held.Value.Identity
		if id.Validate() == nil {
			history, err := s.journal.EstablishedColonyExtent(ctx, domain.GenerationSnapshot{Colony: id.Colony, Map: id.Map, Load: id.Load, Plan: "extent-diagnostics"}, id.Tick)
			if err != nil {
				return httpapi.RoutineStatus{}, err
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
		status.Stage = review.Stage
		status.Roster = review.Roster
		status.LayoutTidy = review.Layout
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
		// Without the dev tick boost each window runs at the player's own
		// speed, Ultrafast under player pacing (#627) when none was chosen
		// (#875); with it every window is boosted Ultrafast.
		FollowPlayerSpeed: !testAcceleration,
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

// serviceClockStepTimeout budgets one scheduler step: the routine review
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
	// Under a running window the journal read is held (wait_ms): the poll
	// returns as soon as a row lands, so a stop is seen near-push instead
	// of at the next cadence. The companion dispatches its tools off the
	// GABP reader (ExtensionDispatchPatch, issue #227; smoke/dispatch
	// measures a read under a held poll), so the routine Worker's dispatch
	// of the successor order and the epoch renew no longer queue behind
	// the held read (the #115 stall that kept PollWait at zero, issue
	// #162). The hold leaves the read its second of the poll budget
	// (NewClockWorker's bound); a call timeout too short for any hold
	// polls unheld at the cadence. A held read that returns empty before
	// its deadline (a native build that ignores wait_ms) falls back to the
	// PollInterval cadence, one read a second.
	wait := min(serviceClockPollWait, lease-time.Second)
	if wait < 0 {
		wait = 0
	}
	return serviceClockTimeoutConfig{Poll: lease, Renew: lease, Step: serviceClockStepTimeout, PollWait: wait, RunningPoll: 0}
}

// serviceClockPollWait bounds the held journal read under a running window.
// It stays under bridge.ClockEventsMaxWaitMs and under the lease-bound poll
// budget less the read's own second, so the poll loop can never be late
// enough to let the native epoch lapse.
const serviceClockPollWait = 4 * time.Second

type serviceClockTimeoutConfig struct{ Poll, Renew, Step, PollWait, RunningPoll time.Duration }

// Session owns the attached worker's drain, including failed startup cleanup.
// Starting these loops does not enable Player or acquire native authority.
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, journal *store.Store, sc serveConfig, timeouts serviceClockTimeoutConfig, wake *buildingruntime.WakeSignal, sections *facts.Store, worldReady func(context.Context, *c.ObservationContext) (bool, error)) (*buildingruntime.ClockWorker, error) {
	profile, routine := sc.profile, sc.routineReviews
	sleeping, cooking, shelter, comfort, expansion, power, temperature := sc.routineSleepingPlans, sc.routineCookingPlans, sc.routineShelterPlans, sc.routineComfortPlans, sc.routineExpansionPlans, sc.routinePowerPlans, sc.routineTemperaturePlans
	workshop := sc.workshopPlans()
	ingredientStorage := sc.routineIngredientStoragePlans && sc.resourceTargetsConfigured()
	research := sc.researchPlans()
	hospital := sc.routineHospitalPlans
	supplies, work, acquisition, defense, tend, rescue, equip := sc.routineSupplyPlans, sc.routineWorkPlans, sc.routineAcquisitionPlans, sc.routineDefensePlans, sc.routineTendPlans, sc.routineRescuePlans, sc.routineEquipPlans
	secureSupplies, repair, clean, gear, medical, foodStorageUpkeep := sc.routineSecureSuppliesPlans, sc.routineRepairPlans, sc.routineCleanPlans, sc.routineGearPlans, sc.routineMedicalPlans, sc.routineFoodStorageUpkeepPlans
	refrigeration := sc.routineRefrigerationPlans
	fireSafety := sc.routineFireSafetyPlans
	lighting := sc.routineLightingPlans
	flooring := sc.routineFlooringPlans
	routes := sc.routineRoutesPlans
	animalContainment, recovery, husbandry, homeCoverage := sc.routineAnimalContainmentPlans, sc.routineRecoveryPlans, sc.routineHusbandryPlans, sc.routineHomeCoveragePlans
	resourceTargets := sc.resourceTargetsConfigured()
	animalFeedPlans := sc.routineAnimalFeedPlans
	fields, bills, foodStorage := sc.routineFieldPlans, sc.routineBillPlans, sc.routineFoodStoragePlans
	prisonerInteraction, populationCustody, stoneShell, defensiveLayout := sc.routinePrisonerInteractionPlans, sc.routinePopulationCustodyPlans, sc.routineStoneShellPlans, sc.routineDefensiveLayoutPlans
	haul, waste, moodRelief, naming, dialog, trade := sc.routineHaulPlans, sc.routineWastePlans, sc.routineMoodPlans, sc.routineNamingPlans, sc.routineDialogPlans, sc.routineTradePlans
	blight := sc.routineBlightPlans
	armory := sc.routineArmoryPlans
	clearance := sc.routineClearancePlans
	shrine := sc.routineShrinePlans
	tidy := sc.routineTidyPlans
	stockpiles := sc.routineStockpilePlans
	config := serviceClockConfig(profile, sc.clockTestAcceleration, defaultClockWindowTicks, uint32(sc.clockBlindTicks))
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
	config.RoutineMethods = session.RoutineMethodsEnabled()
	if (bills || fields || foodStorage || acquisition || work || supplies || sleeping || cooking || shelter || comfort || hospital || expansion || power || temperature || defense || tend || rescue || equip || secureSupplies || repair || fireSafety || clean || haul || waste || blight || armory || clearance || shrine || moodRelief || gear || medical || foodStorageUpkeep || refrigeration || lighting || sc.routineArtPlans || flooring || routes || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || sc.routinePopulationJoinerPlans || homeCoverage || sc.routineShelteringPlans || stoneShell || tidy || stockpiles || defensiveLayout || naming || dialog || trade || resourceTargets || animalFeedPlans) && !routine {
		return nil, errors.New("building plans require routine reviews")
	}
	if routine {
		native, ok := reads.(observation.RoutineSource)
		if !ok {
			return nil, errors.New("routine reviews require typed colony and emergency observations")
		}
		thresholds, capabilities := routineCapabilities(sc)
		// The undraft sweep releases drafts no live plan needs (#939).
		if client, ok := reads.(*bridge.Client); ok {
			if capabilities.Undraft, err = bridge.NewActionsWriter(client); err != nil {
				return nil, err
			}
		}
		reviewer, err := buildingruntime.NewRoutineReviewer(player, native, wallClock{}, thresholds, config.MaxAge, capabilities)
		if err != nil {
			return nil, err
		}
		config.Routine = reviewer
		if bills {
			nativeBills, ok := reads.(buildingruntime.BillPlannerNative)
			if !ok {
				return nil, errors.New("bill plans require typed preview")
			}
			config.CookingBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.CookFood)
			if err != nil {
				return nil, err
			}
			config.PreservationBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.PreserveFood)
			if err != nil {
				return nil, err
			}
			config.ButcherBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.ButcherFood)
			if err != nil {
				return nil, err
			}
			buildingNative, ok := reads.(buildingruntime.RoutineBuildingSource)
			if !ok {
				return nil, errors.New("bill prerequisites require building observations")
			}
			config.Butcher, err = buildingruntime.NewRoutineButcherPlanner(reviewer, buildingNative)
			if err != nil {
				return nil, err
			}
		}
		if sc.routineArtPlans {
			// MaintainArt's pinned sculpture bills (#1190) are their own
			// family, apart from the food bills.
			nativeBills, ok := reads.(buildingruntime.BillPlannerNative)
			if !ok {
				return nil, errors.New("art bills require typed preview")
			}
			config.ArtBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.ArtBill)
			if err != nil {
				return nil, err
			}
		}
		if fields {
			fieldNative, ok := reads.(buildingruntime.FieldNative)
			if !ok {
				return nil, errors.New("field planning requires typed preview")
			}
			config.Fields, err = buildingruntime.NewRoutineFieldPlanner(reviewer, fieldNative)
			if err != nil {
				return nil, err
			}
		}
		if foodStorage {
			fieldNative, ok := reads.(buildingruntime.FieldNative)
			if !ok {
				return nil, errors.New("food storage planning requires typed preview")
			}
			config.FoodStorage, err = buildingruntime.NewRoutineFoodStoragePlanner(reviewer, fieldNative)
			if err != nil {
				return nil, err
			}
		}
		if acquisition {
			config.FoodAcquisition, err = buildingruntime.NewRoutineAcquisitionPlanner(reviewer, policy.EnsureFoodSupply)
			if err != nil {
				return nil, err
			}
			config.PestAcquisition, err = buildingruntime.NewRoutineAcquisitionPlanner(reviewer, policy.ClearPests)
			if err != nil {
				return nil, err
			}
			config.ResourceAcquisition, err = buildingruntime.NewRoutineAcquisitionPlanner(reviewer, policy.MaintainResource)
			if err != nil {
				return nil, err
			}
		}
		if work {
			config.Work, err = buildingruntime.NewRoutineWorkPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if defense {
			defenseNative, ok := reads.(buildingruntime.RoutineDefenseSource)
			if !ok {
				return nil, errors.New("defense plans require typed combat observations")
			}
			config.Defense, err = buildingruntime.NewRoutineDefensePlanner(reviewer, defenseNative)
			if err != nil {
				return nil, err
			}
		}
		if tend {
			tendNative, ok := reads.(buildingruntime.RoutineTendSource)
			if !ok {
				return nil, errors.New("tend plans require typed tend observations")
			}
			config.Tend, err = buildingruntime.NewRoutineTendPlanner(reviewer, tendNative)
			if err != nil {
				return nil, err
			}
		}
		if rescue {
			rescueNative, ok := reads.(buildingruntime.RoutineRescueSource)
			if !ok {
				return nil, errors.New("rescue plans require typed combat observations")
			}
			config.Rescue, err = buildingruntime.NewRoutineRescuePlanner(reviewer, rescueNative)
			if err != nil {
				return nil, err
			}
		}
		if equip {
			equipNative, ok := reads.(buildingruntime.RoutineEquipSource)
			if !ok {
				return nil, errors.New("equip plans require typed equip observations")
			}
			config.Equip, err = buildingruntime.NewRoutineEquipPlanner(reviewer, equipNative)
			if err != nil {
				return nil, err
			}
		}
		if secureSupplies {
			secureSuppliesNative, ok := reads.(buildingruntime.RoutineSecureSuppliesSource)
			if !ok {
				return nil, errors.New("secure supplies plans require typed colony and tend observations")
			}
			config.SecureSupplies, err = buildingruntime.NewRoutineSecureSuppliesPlanner(reviewer, secureSuppliesNative)
			if err != nil {
				return nil, err
			}
		}
		// Shelves (#721) serve the stockpiles SecureSupplies and
		// MaintainResource create, so either planner brings them.
		if secureSupplies || ingredientStorage {
			if shelvesNative, ok := reads.(buildingruntime.RoutineStorageShelvesSource); ok {
				if config.StorageShelves, err = buildingruntime.NewRoutineStorageShelvesPlanner(reviewer, shelvesNative); err != nil {
					return nil, err
				}
			}
		}
		if fireSafety {
			fireNative, ok := reads.(buildingruntime.RoutineFireSafetySource)
			if !ok {
				return nil, errors.New("fire safety plans require typed colony and tend observations")
			}
			config.FireSafety, err = buildingruntime.NewRoutineFireSafetyPlanner(reviewer, fireNative)
			if err != nil {
				return nil, err
			}
		}
		if repair {
			repairNative, ok := reads.(buildingruntime.RoutineRepairSource)
			if !ok {
				return nil, errors.New("repair plans require typed colony and tend observations")
			}
			config.Repair, err = buildingruntime.NewRoutineRepairPlanner(reviewer, repairNative)
			if err != nil {
				return nil, err
			}
		}
		if clean {
			cleanNative, ok := reads.(buildingruntime.RoutineCleanSource)
			if !ok {
				return nil, errors.New("clean plans require typed colony and tend observations")
			}
			config.Clean, err = buildingruntime.NewRoutineCleanPlanner(reviewer, cleanNative)
			if err != nil {
				return nil, err
			}
		}
		if haul {
			haulNative, ok := reads.(buildingruntime.RoutineHaulSource)
			if !ok {
				return nil, errors.New("haul plans require typed colony and tend observations")
			}
			config.Haul, err = buildingruntime.NewRoutineHaulPlanner(reviewer, haulNative)
			if err != nil {
				return nil, err
			}
		}
		if clearance {
			clearanceNative, ok := reads.(buildingruntime.RoutineClearanceSource)
			if !ok {
				return nil, errors.New("clearance plans require typed colony observations")
			}
			config.Clearance, err = buildingruntime.NewRoutineClearancePlanner(reviewer, clearanceNative)
			if err != nil {
				return nil, err
			}
		}
		if shrine {
			shrineNative, ok := reads.(buildingruntime.RoutineShrineSource)
			if !ok {
				return nil, errors.New("shrine plans require typed shrine observations")
			}
			config.Shrine, err = buildingruntime.NewRoutineShrinePlanner(reviewer, shrineNative)
			if err != nil {
				return nil, err
			}
		}
		if armory {
			armoryNative, ok := reads.(buildingruntime.RoutineGearSource)
			if !ok {
				return nil, errors.New("armory plans require typed colony observations")
			}
			config.Armory, err = buildingruntime.NewRoutineArmoryPlanner(reviewer, armoryNative)
			if err != nil {
				return nil, err
			}
		}
		if blight {
			blightNative, ok := reads.(buildingruntime.RoutineBlightSource)
			if !ok {
				return nil, errors.New("blight plans require typed colony observations")
			}
			config.Blight, err = buildingruntime.NewRoutineBlightPlanner(reviewer, blightNative)
			if err != nil {
				return nil, err
			}
		}
		if waste {
			wasteNative, ok := reads.(buildingruntime.RoutineWasteSource)
			if !ok {
				return nil, errors.New("waste plans require typed colony and tend observations")
			}
			config.Waste, err = buildingruntime.NewRoutineWastePlanner(reviewer, wasteNative)
			if err != nil {
				return nil, err
			}
		}
		if moodRelief {
			config.MoodRelief, err = buildingruntime.NewRoutineMoodReliefPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if gear {
			gearNative, ok := reads.(buildingruntime.RoutineGearSource)
			if !ok {
				return nil, errors.New("gear plans require typed colony observations")
			}
			config.Gear, err = buildingruntime.NewRoutineGearPlanner(reviewer, gearNative)
			if err != nil {
				return nil, err
			}
		}
		if medical {
			medicalNative, ok := reads.(buildingruntime.RoutineMedicalSource)
			if !ok {
				return nil, errors.New("medical reserve plans require typed colony observations")
			}
			config.Medical, err = buildingruntime.NewRoutineMedicalPlanner(reviewer, medicalNative)
			if err != nil {
				return nil, err
			}
			config.Surgery, err = buildingruntime.NewRoutineSurgeryPlanner(reviewer)
			if err != nil {
				return nil, err
			}
			// Parts a restore lacks are fabricated where researched (#1168).
			if nativeBills, ok := reads.(buildingruntime.BillPlannerNative); ok {
				config.SurgeryPartBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.SurgeryPartBill)
				if err != nil {
					return nil, err
				}
			}
		}
		if foodStorageUpkeep {
			foodStorageNative, ok := reads.(buildingruntime.RoutineFoodStorageUpkeepSource)
			if !ok {
				return nil, errors.New("food storage upkeep plans require typed colony and resource-source observations")
			}
			config.FoodStorageUpkeep, err = buildingruntime.NewRoutineFoodStorageUpkeepPlanner(reviewer, foodStorageNative)
			if err != nil {
				return nil, err
			}
		}
		if animalContainment {
			containmentNative, ok := reads.(buildingruntime.RoutineBuildingSource)
			if !ok {
				return nil, errors.New("animal containment plans require typed placement previews")
			}
			config.AnimalContainment, err = buildingruntime.NewRoutineAnimalContainmentPlanner(reviewer, containmentNative)
			if err != nil {
				return nil, err
			}
		}
		if recovery {
			config.Recovery, err = buildingruntime.NewRoutineRecoveryPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if husbandry {
			config.Husbandry, err = buildingruntime.NewRoutineHusbandryPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if prisonerInteraction {
			config.PrisonerInteraction, err = buildingruntime.NewRoutinePrisonerInteractionPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if sc.routinePopulationJoinerPlans {
			config.PopulationJoiner, err = buildingruntime.NewRoutinePopulationJoinerPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if populationCustody {
			custodyNative, ok := reads.(buildingruntime.RoutineRescueSource)
			if !ok {
				return nil, errors.New("population custody plans require typed combat observations")
			}
			config.PopulationCustody, err = buildingruntime.NewRoutinePopulationCustodyPlanner(reviewer, custodyNative)
			if err != nil {
				return nil, err
			}
		}
		if sc.routineFirebreakPlans {
			firebreakNative, ok := reads.(buildingruntime.RoutineFirebreakSource)
			if !ok {
				return nil, errors.New("firebreak plans require typed defense site and plant cut census observations")
			}
			if config.Firebreak, err = buildingruntime.NewRoutineFirebreakPlanner(reviewer, firebreakNative); err != nil {
				return nil, err
			}
		}
		if sc.routinePsylinkPlans {
			psylinkNative, ok := reads.(buildingruntime.RoutinePsylinkSource)
			if !ok {
				return nil, errors.New("psylink plans require the typed neuroformer item read")
			}
			if config.Psylink, err = buildingruntime.NewRoutinePsylinkPlanner(reviewer, psylinkNative); err != nil {
				return nil, err
			}
		}
		if sc.routinePermitPlans {
			// MaintainPermits (#1606) plans from the review's royalty read.
			if config.Permits, err = buildingruntime.NewRoutinePermitsPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if sc.routineIdeoRolePlans {
			// MaintainIdeoRoles (#1661) plans from the review's ideology section and pawn rows.
			if config.IdeoRoles, err = buildingruntime.NewRoutineIdeoRolesPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if sc.routineShelteringPlans {
			// MaintainShelter (#1325) plans from the review's rooms; no read of its own.
			if config.MaintainShelter, err = buildingruntime.NewMaintainShelterPlanner(reviewer); err != nil {
				return nil, err
			}
		}
		if homeCoverage {
			config.HomeCoverage, err = buildingruntime.NewRoutineHomeCoveragePlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if stoneShell {
			stoneShellNative, ok := reads.(buildingruntime.RoutineStoneShellSource)
			if !ok {
				return nil, errors.New("stone shell plans require typed wall upgrade site and placement observations")
			}
			config.StoneShell, err = buildingruntime.NewRoutineStoneShellPlanner(reviewer, stoneShellNative)
			if err != nil {
				return nil, err
			}
		}
		if tidy {
			config.Tidy, err = buildingruntime.NewRoutineTidyPlanner(reviewer)
			if err != nil {
				return nil, err
			}
		}
		if stockpiles {
			stockpileNative, ok := reads.(buildingruntime.RoutineStockpileSource)
			if !ok {
				return nil, errors.New("stockpile plans require typed zone target observations")
			}
			config.Stockpiles, err = buildingruntime.NewRoutineStockpilePlanner(reviewer, stockpileNative)
			if err != nil {
				return nil, err
			}
		}
		if defensiveLayout {
			defenseNative, ok := reads.(buildingruntime.RoutineDefenseLayoutSource)
			if !ok {
				return nil, errors.New("defensive layout plans require typed defense site, lines of fire, spatial access, combat pawn and placement observations")
			}
			config.DefenseLayout, err = buildingruntime.NewRoutineDefenseLayoutPlanner(reviewer, defenseNative)
			if err != nil {
				return nil, err
			}
		}
		if research {
			researchNative, ok := reads.(buildingruntime.RoutineResearchSource)
			if !ok {
				return nil, errors.New("research plans require typed research observations")
			}
			config.Research, err = buildingruntime.NewRoutineResearchPlanner(reviewer, researchNative)
			if err != nil {
				return nil, err
			}
		}
		if naming {
			namingNative, ok := reads.(buildingruntime.RoutineNamingSource)
			if !ok {
				return nil, errors.New("naming plans require typed colony observations")
			}
			config.Naming, err = buildingruntime.NewRoutineNamingPlanner(reviewer, namingNative)
			if err != nil {
				return nil, err
			}
		}
		if dialog {
			dialogNative, ok := reads.(buildingruntime.RoutineDialogSource)
			if !ok {
				return nil, errors.New("dialog plans require typed colony observations")
			}
			config.Dialog, err = buildingruntime.NewRoutineDialogPlanner(reviewer, dialogNative, policy.DialogAnswerPolicy{Prefer: policy.DefaultDialogAnswerPrefer})
			if err != nil {
				return nil, err
			}
		}
		if trade {
			tradeNative, ok := reads.(buildingruntime.RoutineTradeSource)
			if !ok {
				return nil, errors.New("trade plans require typed trader and trade sheet observations")
			}
			config.Trade, err = buildingruntime.NewRoutineTradePlanner(reviewer, tradeNative)
			if err != nil {
				return nil, err
			}
		}
		if resourceTargets {
			resourceNative, ok := reads.(buildingruntime.RoutineResourceSource)
			if !ok {
				return nil, errors.New("resource plans require typed colony observations")
			}
			config.Resource, err = buildingruntime.NewRoutineResourcePlanner(reviewer, resourceNative)
			if err != nil {
				return nil, err
			}
		}
		if animalFeedPlans {
			animalFeedNative, ok := reads.(buildingruntime.RoutineResourceSource)
			if !ok {
				return nil, errors.New("animal feed plans require typed colony observations")
			}
			config.AnimalFeed, err = buildingruntime.NewRoutineAnimalFeedPlanner(reviewer, animalFeedNative)
			if err != nil {
				return nil, err
			}
		}
		if supplies {
			source, ok := reads.(buildingruntime.RoutineSupplySource)
			if !ok {
				return nil, errors.New("supply plans require typed supply observations")
			}
			config.Supplies, err = buildingruntime.NewRoutineSupplyPlanner(reviewer, source)
			if err != nil {
				return nil, err
			}
		}
		if sleeping || cooking || shelter || comfort || workshop || hospital || expansion || power || temperature || refrigeration || lighting || flooring || routes {
			source, ok := reads.(buildingruntime.RoutineBuildingSource)
			if !ok {
				return nil, errors.New("building plans require typed placement previews")
			}
			if shelter {
				config.Sleeping, err = buildingruntime.NewRoutineShelterPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			} else if sleeping {
				config.Sleeping, err = buildingruntime.NewRoutineSleepingPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if sleeping {
				config.SleepingUpkeep, err = buildingruntime.NewRoutineSleepingUpkeepPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if temperature {
				config.Temperature, err = buildingruntime.NewRoutineTemperaturePlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if power {
				config.Power, err = buildingruntime.NewRoutinePowerPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if refrigeration {
				config.Refrigeration, err = buildingruntime.NewRoutineRefrigerationPlanner(reviewer, source)
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
				config.CookAheadBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.CookAheadFood)
				if err != nil {
					return nil, err
				}
			}
			if lighting {
				config.Lighting, err = buildingruntime.NewRoutineLightingPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if flooring {
				config.Flooring, err = buildingruntime.NewRoutineFlooringPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if routes {
				config.Routes, err = buildingruntime.NewRoutineRoutesPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if cooking || bills {
				config.Cooking, err = buildingruntime.NewRoutineCookingPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if expansion {
				config.Expansion, err = buildingruntime.NewRoutineExpansionPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if comfort {
				config.Comfort, err = buildingruntime.NewRoutineComfortPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
				config.BasicComfort, err = buildingruntime.NewRoutineBasicComfortPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if workshop {
				config.Workshop, err = buildingruntime.NewRoutineWorkshopPlanner(reviewer, source)
				if err != nil {
					return nil, err
				}
			}
			if ingredientStorage {
				storageNative, ok := reads.(buildingruntime.RoutineIngredientStorageSource)
				if !ok {
					return nil, errors.New("ingredient storage plans require typed bench census and zone preview observations")
				}
				config.IngredientStorage, err = buildingruntime.NewRoutineIngredientStoragePlanner(reviewer, storageNative)
				if err != nil {
					return nil, err
				}
			}
			if hospital {
				config.Hospital, err = buildingruntime.NewRoutineHospitalPlanner(reviewer, source)
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
		PollWait: timeouts.PollWait, RunningPollInterval: timeouts.RunningPoll, Wake: wake,
	})
}

// routineCapabilities derives the routine policy thresholds and the method
// capabilities a composed serve declares from its enabled families. Every
// family whose planner acts only while the development ranking selected its
// goal must declare that goal here, or the review ranks it method_unavailable
// and the planner never runs.
func routineCapabilities(sc serveConfig) (policy.RoutinePolicy, buildingruntime.RoutineCapabilities) {
	thresholds := policy.DefaultRoutinePolicy()
	if !sc.footholdComposed() {
		// Foothold's exit criteria (shelter, cooking, food storage, basic
		// defense) have no planner in this composition, so the measured
		// stage could never climb and every staged family composed here
		// would never be raised: its goals, planners and the clock's work
		// all wait on a stage nothing can reach. Apply every stage's goals.
		thresholds.Stage.Floor = policy.StageDevelopment
	}
	capabilities := buildingruntime.RoutineCapabilities{LayoutOverlay: sc.layoutOverlay}
	if sc.routineAcquisitionPlans || sc.routineFieldPlans || sc.routineBillPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureFoodSupply)
	}
	if sc.routineAcquisitionPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainResource, policy.ClearPests)
	}
	if sc.routineBillPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureCooking, policy.MaintainButcherSpot)
	}
	if sc.routineFoodStoragePlans || sc.routineBillPlans || sc.routineFoodStorageUpkeepPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFoodStorage)
	}
	if sc.routineTemperaturePlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureTemperatureSafety)
	}
	if sc.routinePowerPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureBasicPower)
	}
	if sc.routineRefrigerationPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainRefrigeration)
	}
	if sc.routineLightingPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainLighting)
	}
	if sc.routineArtPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainArt)
	}
	if sc.routineFlooringPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFlooring)
	}
	if sc.routineRoutesPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainRoutes)
	}
	if sc.routineComfortPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureComfort)
	}
	if sc.routineSleepingPlans || sc.routineExpansionPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHousing)
	}
	if sc.routineAnimalContainmentPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainAnimalContainment)
	}
	if sc.routineSecureSuppliesPlans {
		capabilities.Methods = append(capabilities.Methods, policy.SecureSupplies)
	}
	if sc.routineRepairPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainEssentialRepairs)
	}
	if sc.routineFireSafetyPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFireSafety)
	}
	if sc.routineCleanPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainCleanFacilities)
	}
	if sc.routineHaulPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainStorage)
	}
	if sc.routineWastePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainWaste)
	}
	if sc.routineGearPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainEquipment)
	}
	if sc.routineClearancePlans {
		capabilities.Methods = append(capabilities.Methods, policy.ClearHomeObstructions)
	}
	if sc.routineShrinePlans {
		capabilities.Methods = append(capabilities.Methods, policy.ClearAncientShrine)
	}
	if sc.routineBlightPlans {
		capabilities.Methods = append(capabilities.Methods, policy.RemoveBlight)
	}
	if sc.routineRecoveryPlans {
		capabilities.Methods = append(capabilities.Methods, policy.RecoverDisasterServices)
	}
	if sc.routineHusbandryPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHerd)
	}
	if sc.routinePrisonerInteractionPlans || sc.routinePopulationCustodyPlans || sc.routinePopulationJoinerPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainPopulation)
	}
	if sc.routineHomeCoveragePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHomeCoverage)
	}
	if sc.routineShelteringPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainShelter)
	}
	if sc.routineFirebreakPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFirebreak)
	}
	if sc.routinePsylinkPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainPsylink)
	}
	if sc.routinePermitPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainPermits)
	}
	if sc.routineIdeoRolePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainIdeoRoles)
	}
	if sc.routineStoneShellPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainStoneShell)
	}
	if sc.routineTidyPlans {
		capabilities.Methods = append(capabilities.Methods, policy.TidyLayout)
	}
	if sc.routineStockpilePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainStockpiles)
	}
	if sc.routineDefensiveLayoutPlans {
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
		// Acquisition already declares it (the wood floor, #728).
		if !slices.Contains(capabilities.Methods, policy.MaintainResource) {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainResource)
		}
	} else {
		// No resource family: no floors, so trade and the workshop do not
		// chase targets nothing produces.
		thresholds.ResourceTargets, thresholds.StoneBlockTarget = nil, 0
	}
	if sc.routineAnimalFeedPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainAnimalFeed)
	}
	if sc.routineMedicalPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainMedicalReserves, policy.MaintainSurgery)
	}
	if sc.routineTradePlans {
		thresholds.Trade = policy.RoutineTradePolicy{ComponentTarget: policy.DefaultResourceTargets()[policy.ComponentResource]}
		capabilities.Methods = append(capabilities.Methods, policy.TradeWithCaravan)
	}
	// The equip planner is EnsureBasicDefense's method: without this
	// declaration the priority-3 goal reviews as method_unavailable, never
	// wins a development slot, and every equip commit is refused (colony-2
	// ended with every survivor unarmed beside loose bows).
	if sc.routineEquipPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureBasicDefense)
	}
	return thresholds, capabilities
}
