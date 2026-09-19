package main

import (
	"context"
	"errors"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
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
	}
	if review.Revision != 0 {
		development := review.Development.State()
		status.Development = &development
	}
	return status, nil
}

type serviceClockReads interface {
	buildingruntime.ClockWindowNative
	buildingruntime.ClockEventNative
}

// parseClockSpeed maps the validated --clock-speed flag value (parseServe
// already rejects anything else) to the native Clock.Speed enum.
func parseClockSpeed(speed string) k.Speed {
	switch speed {
	case "Fast":
		return k.Speed_SPEED_FAST
	case "Superfast":
		return k.Speed_SPEED_SUPERFAST
	case "Ultrafast":
		return k.Speed_SPEED_ULTRAFAST
	default:
		return k.Speed_SPEED_NORMAL
	}
}

// Window policy. A colony window runs at least defaultClockWindowTicks (a
// little over one game hour) before it pauses for a full review;
// --clock-window-ticks widens or narrows that floor up to
// maxClockWindowTicks (one game day), never unbounded. Faster clocks run
// the floor in less wall time than a review takes, so the scheduler grows
// the window to the ticks the configured speed runs in
// --clock-window-seconds of wall time, or in the pause it observes between
// windows if that is longer, still capped at maxClockWindowTicks (issue
// #126; buildingruntime.ClockWindowSizing). Watched outcomes, danger and
// player input stop a window early regardless, so a wider window costs
// review latency only while nothing happens. A raid runs in
// combatClockWindowTicks windows so the defense planner re-targets between
// them; that bound is fixed.
const (
	defaultClockWindowTicks   = 2500
	maxClockWindowTicks       = 60000
	combatClockWindowTicks    = 300
	defaultClockWindowSeconds = 2.0
)

// clockTicksPerSecond is the nominal wall tick rate of a speed: RimWorld
// ticks 60 times a game second at Normal and multiplies that by 3, 6 and 15
// for Fast, Superfast and Ultrafast. The headless test boost (an Ultrafast
// epoch with test acceleration) ticks as fast as the game thread allows;
// acceleratedClockTicksPerSecond is the rate #109 measured.
func clockTicksPerSecond(speed k.Speed, testAcceleration bool) float64 {
	if testAcceleration && speed == k.Speed_SPEED_ULTRAFAST {
		return acceleratedClockTicksPerSecond
	}
	switch speed {
	case k.Speed_SPEED_FAST:
		return 180
	case k.Speed_SPEED_SUPERFAST:
		return 360
	case k.Speed_SPEED_ULTRAFAST:
		return 900
	default:
		return 60
	}
}

const acceleratedClockTicksPerSecond = 7000

func serviceClockConfig(profile string, speed k.Speed, testAcceleration bool, windowTicks uint32, windowSeconds float64) buildingruntime.ClockSchedulerConfig {
	return buildingruntime.ClockSchedulerConfig{
		Window: buildingruntime.ClockWindowSizing{Seconds: windowSeconds, TicksPerSecond: clockTicksPerSecond(speed, testAcceleration), MaxTicks: maxClockWindowTicks},
		// MaxAge bounds how stale the admission reads (status, emergency)
		// may be by the time EvaluateClockWindow admits a window. The
		// planner facts are bound by tick instead (FactsTick), so the step
		// budget no longer constrains it; it stays at the step timeout so
		// a slow admission read under peer load still admits.
		Profile: profile, MaxAge: serviceClockStepTimeout,
		CombatMaxTicks: min(combatClockWindowTicks, windowTicks),
		Start: bridge.ClockStart{Speed: speed, TestAcceleration: testAcceleration, LeaseMS: 30000, MaxTicks: windowTicks,
			Policy: &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(),
				HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.5),
				HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}},
	}
}

// serviceClockStepTimeout budgets one scheduler step: the routine review
// census plus every composed planner's native reads. It matches the Player's
// CallTimeout (serve_building.go) and is independent of the epoch lease. Under
// peer load (several headless RimWorld instances on one machine) a planner
// read alone can exceed the lease-bound 7s renew budget, which used to time
// out every step so no clock window was ever admitted (issue #73).
const serviceClockStepTimeout = 30 * time.Second

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
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, journal *store.Store, sc serveConfig, timeouts serviceClockTimeoutConfig, wake *buildingruntime.WakeSignal, facts *bridge.FactCache) (*buildingruntime.ClockWorker, error) {
	profile, clockSpeed, routine := sc.profile, sc.clockSpeed, sc.routineReviews
	sleeping, cooking, shelter, comfort, expansion, power, temperature := sc.routineSleepingPlans, sc.routineCookingPlans, sc.routineShelterPlans, sc.routineComfortPlans, sc.routineExpansionPlans, sc.routinePowerPlans, sc.routineTemperaturePlans
	workshop := sc.routineWorkshopPlans && len(sc.routineResourceTargets.Map()) > 0
	ingredientStorage := sc.routineIngredientStoragePlans && len(sc.routineResourceTargets.Map()) > 0
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
	caravanJourneyTracking, researchTarget, resourceTargets := sc.caravanJourneyTracking, sc.routineResearchTarget, sc.routineResourceTargets.Map()
	animalFeedPlans, productionPolicyPlans := sc.routineAnimalFeedPlans, sc.routineProductionPolicyPlans
	fields, bills, foodStorage := sc.routineFieldPlans, sc.routineBillPlans, sc.routineFoodStoragePlans
	prisonerInteraction, populationCustody, stoneShell, defensiveLayout := sc.routinePrisonerInteractionPlans, sc.routinePopulationCustodyPlans, sc.routineStoneShellPlans, sc.routineDefensiveLayoutPlans
	haul, waste, moodRelief, naming, dialog := sc.routineHaulPlans, sc.routineWastePlans, sc.routineMoodPlans, sc.routineNamingPlans, sc.routineDialogPlans
	config := serviceClockConfig(profile, parseClockSpeed(clockSpeed), sc.clockTestAcceleration, uint32(sc.clockWindowTicks), sc.clockWindowSeconds)
	config.Facts = facts
	config.Worker = true
	config.RoutineMethods = session.RoutineMethodsEnabled()
	if caravanJourneyTracking {
		native, ok := reads.(buildingruntime.CaravanJourneyNative)
		if !ok {
			return nil, errors.New("caravan journey tracking requires typed world progression and home colonist observations")
		}
		tracker, err := buildingruntime.NewCaravanJourneyTracker(player, native, journal, wallClock{}, config.MaxAge)
		if err != nil {
			return nil, err
		}
		config.CaravanJourney = tracker
	}
	if (bills || fields || foodStorage || acquisition || work || supplies || sleeping || cooking || shelter || comfort || hospital || expansion || power || temperature || defense || tend || rescue || equip || secureSupplies || repair || fireSafety || clean || haul || waste || moodRelief || gear || medical || foodStorageUpkeep || refrigeration || lighting || flooring || routes || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || homeCoverage || stoneShell || defensiveLayout || naming || dialog || researchTarget != "" || len(resourceTargets) > 0 || animalFeedPlans || productionPolicyPlans) && !routine {
		return nil, errors.New("building plans require routine reviews")
	}
	if routine {
		native, ok := reads.(observation.RoutineSource)
		if !ok {
			return nil, errors.New("routine reviews require typed colony and emergency observations")
		}
		thresholds, capabilities := routineCapabilities(sc)
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
			config.WoodAcquisition, err = buildingruntime.NewRoutineAcquisitionPlanner(reviewer, policy.MaintainWood)
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
			moodReliefNative, ok := reads.(buildingruntime.RoutineMoodReliefSource)
			if !ok {
				return nil, errors.New("mood plans require typed colony observations")
			}
			config.MoodRelief, err = buildingruntime.NewRoutineMoodReliefPlanner(reviewer, moodReliefNative)
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
		if homeCoverage {
			homeCoverageNative, ok := reads.(buildingruntime.RoutineHomeCoverageSource)
			if !ok {
				return nil, errors.New("home coverage plans require typed colony and construction observations")
			}
			config.HomeCoverage, err = buildingruntime.NewRoutineHomeCoveragePlanner(reviewer, homeCoverageNative)
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
			config.Dialog, err = buildingruntime.NewRoutineDialogPlanner(reviewer, dialogNative, policy.DialogAnswerPolicy{Prefer: splitDialogPrefer(sc.routineDialogPrefer)})
			if err != nil {
				return nil, err
			}
		}
		if len(resourceTargets) > 0 {
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
		if productionPolicyPlans {
			productionPolicyNative, ok := reads.(buildingruntime.RoutineProductionPolicySource)
			if !ok {
				return nil, errors.New("production policy plans require typed production policy observations")
			}
			config.ProductionPolicy, err = buildingruntime.NewRoutineProductionPolicyPlanner(reviewer, productionPolicyNative)
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
			var excavation buildingruntime.RoutineExcavationSource
			if shelter || expansion {
				excavation, ok = reads.(buildingruntime.RoutineExcavationSource)
				if !ok {
					return nil, errors.New("shelter and expansion plans require typed excavation site reads")
				}
			}
			if shelter {
				config.Sleeping, err = buildingruntime.NewRoutineShelterPlanner(reviewer, source, excavation)
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
				config.Expansion, err = buildingruntime.NewRoutineExpansionPlanner(reviewer, source, excavation)
				if err != nil {
					return nil, err
				}
			}
			if comfort {
				config.Comfort, err = buildingruntime.NewRoutineComfortPlanner(reviewer, source)
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
	thresholds.MaxDevelopmentProjects = sc.routineProjectLimit
	capabilities := buildingruntime.RoutineCapabilities{}
	if sc.routineAcquisitionPlans || sc.routineFieldPlans || sc.routineBillPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureFoodSupply)
	}
	if sc.routineFoodStoragePlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureFoodStorage)
	}
	if sc.routineAcquisitionPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainWood)
	}
	if sc.routineBillPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureCooking)
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
	if sc.routineFlooringPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainFlooring)
	}
	if sc.routineRoutesPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainRoutes)
	}
	if sc.routineComfortPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureComfort)
	}
	if sc.routineSleepingPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainSleeping)
	}
	if sc.routineExpansionPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureExpansion)
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
	if sc.routineRecoveryPlans {
		capabilities.Methods = append(capabilities.Methods, policy.RecoverDisasterServices)
	}
	if sc.routineHusbandryPlans {
		thresholds.AllowSlaughter = sc.routineAllowSlaughter
		thresholds.AllowRelease = sc.routineAllowRelease
		thresholds.HerdPopulationMax = sc.routineHerdPopulationMax.Map()
		thresholds.HerdPopulationMin = sc.routineHerdPopulationMin.Map()
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHerd)
	}
	if sc.routinePrisonerInteractionPlans || sc.routinePopulationCustodyPlans {
		thresholds.PrisonerReleaseAfterDays = sc.routinePrisonerReleaseAfterDays
		capabilities.Methods = append(capabilities.Methods, policy.MaintainPopulation)
	}
	if sc.routineHomeCoveragePlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainHomeCoverage)
	}
	if sc.routineStoneShellPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainStoneShell)
	}
	if sc.routineDefensiveLayoutPlans {
		thresholds.DefensiveLayout = true
		capabilities.Methods = append(capabilities.Methods, policy.EnsureDefensiveLayout)
	}
	thresholds.ResearchLadder = nil
	if sc.researchPlans() {
		thresholds.ResearchTarget = sc.routineResearchTarget
		thresholds.ResearchLadder = sc.researchLadder()
		capabilities.Methods = append(capabilities.Methods, policy.EnsureResearch)
	}
	if len(sc.routineResourceTargets.Map()) > 0 {
		thresholds.ResourceTargets = sc.routineResourceTargets.Map()
		capabilities.Methods = append(capabilities.Methods, policy.MaintainResource)
	}
	if sc.routineAnimalFeedPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainAnimalFeed)
	}
	if sc.routineMedicalPlans {
		capabilities.Methods = append(capabilities.Methods, policy.MaintainMedicalReserves)
	}
	if sc.routineProductionPolicyPlans {
		thresholds.ResourceReserves = sc.routineResourceReserves.Map()
		thresholds.StoppedResources = sc.routineStoppedResources.Slice()
		capabilities.Methods = append(capabilities.Methods, policy.ProductionPolicy)
	}
	return thresholds, capabilities
}

// splitDialogPrefer parses --routine-dialog-prefer: comma-separated label
// patterns, blanks dropped, order kept.
func splitDialogPrefer(raw string) []string {
	var out []string
	for _, pattern := range strings.Split(raw, ",") {
		if pattern = strings.TrimSpace(pattern); pattern != "" {
			out = append(out, pattern)
		}
	}
	return out
}
