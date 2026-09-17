package main

import (
	"context"
	"errors"
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
	default:
		return k.Speed_SPEED_NORMAL
	}
}

func serviceClockConfig(profile string, speed k.Speed) buildingruntime.ClockSchedulerConfig {
	return buildingruntime.ClockSchedulerConfig{
		// MaxAge bounds how stale the facts read during Step() may be by the
		// time EvaluateClockWindow admits a window. It must cover the whole
		// step budget (serviceClockTimeouts): the routine census plus every
		// composed planner's native reads run inside one step, and facts read
		// at its start are admitted at its end. A MaxAge shorter than the
		// step refuses with stale_facts before the write is ever attempted.
		Profile: profile, MaxAge: serviceClockStepTimeout,
		// A raid runs in 300-tick combat windows so the defense planner can
		// re-target between them; colony windows keep the 600-tick budget.
		CombatMaxTicks: 300,
		Start: bridge.ClockStart{Speed: speed, LeaseMS: 30000, MaxTicks: 600,
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
	return serviceClockTimeoutConfig{Poll: lease, Renew: lease, Step: serviceClockStepTimeout}
}

type serviceClockTimeoutConfig struct{ Poll, Renew, Step time.Duration }

// Session owns the attached worker's drain, including failed startup cleanup.
// Starting these loops does not enable Player or acquire native authority.
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, journal *store.Store, sc serveConfig, timeouts serviceClockTimeoutConfig) error {
	profile, clockSpeed, routine := sc.profile, sc.clockSpeed, sc.routineReviews
	sleeping, cooking, shelter, comfort, expansion, power, temperature := sc.routineSleepingPlans, sc.routineCookingPlans, sc.routineShelterPlans, sc.routineComfortPlans, sc.routineExpansionPlans, sc.routinePowerPlans, sc.routineTemperaturePlans
	workshop := sc.routineWorkshopPlans && len(sc.routineResourceTargets.Map()) > 0
	supplies, work, acquisition, defense, tend, rescue, equip := sc.routineSupplyPlans, sc.routineWorkPlans, sc.routineAcquisitionPlans, sc.routineDefensePlans, sc.routineTendPlans, sc.routineRescuePlans, sc.routineEquipPlans
	secureSupplies, repair, clean, gear, medical, foodStorageUpkeep := sc.routineSecureSuppliesPlans, sc.routineRepairPlans, sc.routineCleanPlans, sc.routineGearPlans, sc.routineMedicalPlans, sc.routineFoodStorageUpkeepPlans
	refrigeration := sc.routineRefrigerationPlans
	fireSafety := sc.routineFireSafetyPlans
	lighting := sc.routineLightingPlans
	animalContainment, recovery, husbandry, homeCoverage := sc.routineAnimalContainmentPlans, sc.routineRecoveryPlans, sc.routineHusbandryPlans, sc.routineHomeCoveragePlans
	caravanJourneyTracking, researchTarget, resourceTargets := sc.caravanJourneyTracking, sc.routineResearchTarget, sc.routineResourceTargets.Map()
	animalFeedPlans, productionPolicyPlans := sc.routineAnimalFeedPlans, sc.routineProductionPolicyPlans
	fields, bills, foodStorage := sc.routineFieldPlans, sc.routineBillPlans, sc.routineFoodStoragePlans
	prisonerInteraction, populationCustody, stoneShell, defensiveLayout := sc.routinePrisonerInteractionPlans, sc.routinePopulationCustodyPlans, sc.routineStoneShellPlans, sc.routineDefensiveLayoutPlans
	haul, waste, moodRelief, naming := sc.routineHaulPlans, sc.routineWastePlans, sc.routineMoodPlans, sc.routineNamingPlans
	config := serviceClockConfig(profile, parseClockSpeed(clockSpeed))
	config.RoutineMethods = session.RoutineMethodsEnabled()
	if caravanJourneyTracking {
		native, ok := reads.(buildingruntime.CaravanJourneyNative)
		if !ok {
			return errors.New("caravan journey tracking requires typed world progression and home colonist observations")
		}
		tracker, err := buildingruntime.NewCaravanJourneyTracker(player, native, journal, wallClock{}, config.MaxAge)
		if err != nil {
			return err
		}
		config.CaravanJourney = tracker
	}
	if (bills || fields || foodStorage || acquisition || work || supplies || sleeping || cooking || shelter || comfort || expansion || power || temperature || defense || tend || rescue || equip || secureSupplies || repair || fireSafety || clean || haul || waste || moodRelief || gear || medical || foodStorageUpkeep || refrigeration || lighting || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || homeCoverage || stoneShell || defensiveLayout || naming || researchTarget != "" || len(resourceTargets) > 0 || animalFeedPlans || productionPolicyPlans) && !routine {
		return errors.New("building plans require routine reviews")
	}
	if routine {
		native, ok := reads.(observation.RoutineSource)
		if !ok {
			return errors.New("routine reviews require typed colony and emergency observations")
		}
		thresholds, capabilities := routineCapabilities(sc)
		reviewer, err := buildingruntime.NewRoutineReviewer(player, native, wallClock{}, thresholds, config.MaxAge, capabilities)
		if err != nil {
			return err
		}
		config.Routine = reviewer
		if bills {
			nativeBills, ok := reads.(buildingruntime.BillPlannerNative)
			if !ok {
				return errors.New("bill plans require typed preview")
			}
			config.CookingBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.CookFood)
			if err != nil {
				return err
			}
			config.PreservationBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.PreserveFood)
			if err != nil {
				return err
			}
			config.ButcherBills, err = buildingruntime.NewRoutineBillPlanner(reviewer, nativeBills, policy.ButcherFood)
			if err != nil {
				return err
			}
			buildingNative, ok := reads.(buildingruntime.RoutineBuildingSource)
			if !ok {
				return errors.New("bill prerequisites require building observations")
			}
			config.Butcher, err = buildingruntime.NewRoutineButcherPlanner(reviewer, buildingNative)
			if err != nil {
				return err
			}
		}
		if fields {
			fieldNative, ok := reads.(buildingruntime.FieldNative)
			if !ok {
				return errors.New("field planning requires typed preview")
			}
			config.Fields, err = buildingruntime.NewRoutineFieldPlanner(reviewer, fieldNative)
			if err != nil {
				return err
			}
		}
		if foodStorage {
			fieldNative, ok := reads.(buildingruntime.FieldNative)
			if !ok {
				return errors.New("food storage planning requires typed preview")
			}
			config.FoodStorage, err = buildingruntime.NewRoutineFoodStoragePlanner(reviewer, fieldNative)
			if err != nil {
				return err
			}
		}
		if acquisition {
			config.FoodAcquisition, err = buildingruntime.NewRoutineAcquisitionPlanner(reviewer, policy.EnsureFoodSupply)
			if err != nil {
				return err
			}
			config.WoodAcquisition, err = buildingruntime.NewRoutineAcquisitionPlanner(reviewer, policy.MaintainWood)
			if err != nil {
				return err
			}
		}
		if work {
			config.Work, err = buildingruntime.NewRoutineWorkPlanner(reviewer)
			if err != nil {
				return err
			}
		}
		if defense {
			defenseNative, ok := reads.(buildingruntime.RoutineDefenseSource)
			if !ok {
				return errors.New("defense plans require typed combat observations")
			}
			config.Defense, err = buildingruntime.NewRoutineDefensePlanner(reviewer, defenseNative)
			if err != nil {
				return err
			}
		}
		if tend {
			tendNative, ok := reads.(buildingruntime.RoutineTendSource)
			if !ok {
				return errors.New("tend plans require typed tend observations")
			}
			config.Tend, err = buildingruntime.NewRoutineTendPlanner(reviewer, tendNative)
			if err != nil {
				return err
			}
		}
		if rescue {
			rescueNative, ok := reads.(buildingruntime.RoutineRescueSource)
			if !ok {
				return errors.New("rescue plans require typed combat observations")
			}
			config.Rescue, err = buildingruntime.NewRoutineRescuePlanner(reviewer, rescueNative)
			if err != nil {
				return err
			}
		}
		if equip {
			equipNative, ok := reads.(buildingruntime.RoutineEquipSource)
			if !ok {
				return errors.New("equip plans require typed equip observations")
			}
			config.Equip, err = buildingruntime.NewRoutineEquipPlanner(reviewer, equipNative)
			if err != nil {
				return err
			}
		}
		if secureSupplies {
			secureSuppliesNative, ok := reads.(buildingruntime.RoutineSecureSuppliesSource)
			if !ok {
				return errors.New("secure supplies plans require typed colony and tend observations")
			}
			config.SecureSupplies, err = buildingruntime.NewRoutineSecureSuppliesPlanner(reviewer, secureSuppliesNative)
			if err != nil {
				return err
			}
		}
		if fireSafety {
			fireNative, ok := reads.(buildingruntime.RoutineFireSafetySource)
			if !ok {
				return errors.New("fire safety plans require typed colony and tend observations")
			}
			config.FireSafety, err = buildingruntime.NewRoutineFireSafetyPlanner(reviewer, fireNative)
			if err != nil {
				return err
			}
		}
		if repair {
			repairNative, ok := reads.(buildingruntime.RoutineRepairSource)
			if !ok {
				return errors.New("repair plans require typed colony and tend observations")
			}
			config.Repair, err = buildingruntime.NewRoutineRepairPlanner(reviewer, repairNative)
			if err != nil {
				return err
			}
		}
		if clean {
			cleanNative, ok := reads.(buildingruntime.RoutineCleanSource)
			if !ok {
				return errors.New("clean plans require typed colony and tend observations")
			}
			config.Clean, err = buildingruntime.NewRoutineCleanPlanner(reviewer, cleanNative)
			if err != nil {
				return err
			}
		}
		if haul {
			haulNative, ok := reads.(buildingruntime.RoutineHaulSource)
			if !ok {
				return errors.New("haul plans require typed colony and tend observations")
			}
			config.Haul, err = buildingruntime.NewRoutineHaulPlanner(reviewer, haulNative)
			if err != nil {
				return err
			}
		}
		if waste {
			wasteNative, ok := reads.(buildingruntime.RoutineWasteSource)
			if !ok {
				return errors.New("waste plans require typed colony and tend observations")
			}
			config.Waste, err = buildingruntime.NewRoutineWastePlanner(reviewer, wasteNative)
			if err != nil {
				return err
			}
		}
		if moodRelief {
			moodReliefNative, ok := reads.(buildingruntime.RoutineMoodReliefSource)
			if !ok {
				return errors.New("mood plans require typed colony observations")
			}
			config.MoodRelief, err = buildingruntime.NewRoutineMoodReliefPlanner(reviewer, moodReliefNative)
			if err != nil {
				return err
			}
		}
		if gear {
			gearNative, ok := reads.(buildingruntime.RoutineGearSource)
			if !ok {
				return errors.New("gear plans require typed colony observations")
			}
			config.Gear, err = buildingruntime.NewRoutineGearPlanner(reviewer, gearNative)
			if err != nil {
				return err
			}
		}
		if medical {
			medicalNative, ok := reads.(buildingruntime.RoutineMedicalSource)
			if !ok {
				return errors.New("medical reserve plans require typed colony observations")
			}
			config.Medical, err = buildingruntime.NewRoutineMedicalPlanner(reviewer, medicalNative)
			if err != nil {
				return err
			}
		}
		if foodStorageUpkeep {
			foodStorageNative, ok := reads.(buildingruntime.RoutineFoodStorageUpkeepSource)
			if !ok {
				return errors.New("food storage upkeep plans require typed colony and resource-source observations")
			}
			config.FoodStorageUpkeep, err = buildingruntime.NewRoutineFoodStorageUpkeepPlanner(reviewer, foodStorageNative)
			if err != nil {
				return err
			}
		}
		if animalContainment {
			containmentNative, ok := reads.(buildingruntime.RoutineBuildingSource)
			if !ok {
				return errors.New("animal containment plans require typed placement previews")
			}
			config.AnimalContainment, err = buildingruntime.NewRoutineAnimalContainmentPlanner(reviewer, containmentNative)
			if err != nil {
				return err
			}
		}
		if recovery {
			config.Recovery, err = buildingruntime.NewRoutineRecoveryPlanner(reviewer)
			if err != nil {
				return err
			}
		}
		if husbandry {
			config.Husbandry, err = buildingruntime.NewRoutineHusbandryPlanner(reviewer)
			if err != nil {
				return err
			}
		}
		if prisonerInteraction {
			config.PrisonerInteraction, err = buildingruntime.NewRoutinePrisonerInteractionPlanner(reviewer)
			if err != nil {
				return err
			}
		}
		if populationCustody {
			custodyNative, ok := reads.(buildingruntime.RoutineRescueSource)
			if !ok {
				return errors.New("population custody plans require typed combat observations")
			}
			config.PopulationCustody, err = buildingruntime.NewRoutinePopulationCustodyPlanner(reviewer, custodyNative)
			if err != nil {
				return err
			}
		}
		if homeCoverage {
			homeCoverageNative, ok := reads.(buildingruntime.RoutineHomeCoverageSource)
			if !ok {
				return errors.New("home coverage plans require typed colony and construction observations")
			}
			config.HomeCoverage, err = buildingruntime.NewRoutineHomeCoveragePlanner(reviewer, homeCoverageNative)
			if err != nil {
				return err
			}
		}
		if stoneShell {
			stoneShellNative, ok := reads.(buildingruntime.RoutineStoneShellSource)
			if !ok {
				return errors.New("stone shell plans require typed wall upgrade site and placement observations")
			}
			config.StoneShell, err = buildingruntime.NewRoutineStoneShellPlanner(reviewer, stoneShellNative)
			if err != nil {
				return err
			}
		}
		if defensiveLayout {
			defenseNative, ok := reads.(buildingruntime.RoutineDefenseLayoutSource)
			if !ok {
				return errors.New("defensive layout plans require typed defense site, lines of fire, spatial access, combat pawn and placement observations")
			}
			config.DefenseLayout, err = buildingruntime.NewRoutineDefenseLayoutPlanner(reviewer, defenseNative)
			if err != nil {
				return err
			}
		}
		if researchTarget != "" {
			researchNative, ok := reads.(buildingruntime.RoutineResearchSource)
			if !ok {
				return errors.New("research plans require typed research observations")
			}
			config.Research, err = buildingruntime.NewRoutineResearchPlanner(reviewer, researchNative)
			if err != nil {
				return err
			}
		}
		if naming {
			namingNative, ok := reads.(buildingruntime.RoutineNamingSource)
			if !ok {
				return errors.New("naming plans require typed colony observations")
			}
			config.Naming, err = buildingruntime.NewRoutineNamingPlanner(reviewer, namingNative)
			if err != nil {
				return err
			}
		}
		if len(resourceTargets) > 0 {
			resourceNative, ok := reads.(buildingruntime.RoutineResourceSource)
			if !ok {
				return errors.New("resource plans require typed colony observations")
			}
			config.Resource, err = buildingruntime.NewRoutineResourcePlanner(reviewer, resourceNative)
			if err != nil {
				return err
			}
		}
		if animalFeedPlans {
			animalFeedNative, ok := reads.(buildingruntime.RoutineResourceSource)
			if !ok {
				return errors.New("animal feed plans require typed colony observations")
			}
			config.AnimalFeed, err = buildingruntime.NewRoutineAnimalFeedPlanner(reviewer, animalFeedNative)
			if err != nil {
				return err
			}
		}
		if productionPolicyPlans {
			productionPolicyNative, ok := reads.(buildingruntime.RoutineProductionPolicySource)
			if !ok {
				return errors.New("production policy plans require typed production policy observations")
			}
			config.ProductionPolicy, err = buildingruntime.NewRoutineProductionPolicyPlanner(reviewer, productionPolicyNative)
			if err != nil {
				return err
			}
		}
		if supplies {
			source, ok := reads.(buildingruntime.RoutineSupplySource)
			if !ok {
				return errors.New("supply plans require typed supply observations")
			}
			config.Supplies, err = buildingruntime.NewRoutineSupplyPlanner(reviewer, source)
			if err != nil {
				return err
			}
		}
		if sleeping || cooking || shelter || comfort || workshop || expansion || power || temperature || refrigeration || lighting {
			source, ok := reads.(buildingruntime.RoutineBuildingSource)
			if !ok {
				return errors.New("building plans require typed placement previews")
			}
			var excavation buildingruntime.RoutineExcavationSource
			if shelter || expansion {
				excavation, ok = reads.(buildingruntime.RoutineExcavationSource)
				if !ok {
					return errors.New("shelter and expansion plans require typed excavation site reads")
				}
			}
			if shelter {
				config.Sleeping, err = buildingruntime.NewRoutineShelterPlanner(reviewer, source, excavation)
				if err != nil {
					return err
				}
			} else if sleeping {
				config.Sleeping, err = buildingruntime.NewRoutineSleepingPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if temperature {
				config.Temperature, err = buildingruntime.NewRoutineTemperaturePlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if power {
				config.Power, err = buildingruntime.NewRoutinePowerPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if refrigeration {
				config.Refrigeration, err = buildingruntime.NewRoutineRefrigerationPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if lighting {
				config.Lighting, err = buildingruntime.NewRoutineLightingPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if cooking || bills {
				config.Cooking, err = buildingruntime.NewRoutineCookingPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if expansion {
				config.Expansion, err = buildingruntime.NewRoutineExpansionPlanner(reviewer, source, excavation)
				if err != nil {
					return err
				}
			}
			if comfort {
				config.Comfort, err = buildingruntime.NewRoutineComfortPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if workshop {
				config.Workshop, err = buildingruntime.NewRoutineWorkshopPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
		}
	}
	scheduler, err := buildingruntime.NewClockScheduler(player, session, reads, config, wallClock{})
	if err != nil {
		return err
	}
	_, err = buildingruntime.NewClockWorker(ctx, scheduler, reads, buildingruntime.ClockWorkerConfig{
		PollInterval: time.Second, RenewInterval: 5 * time.Second, StepInterval: time.Second,
		MaxBackoff: 10 * time.Second, PollTimeout: timeouts.Poll, RenewTimeout: timeouts.Renew, StepTimeout: timeouts.Step, PageLimit: 128,
	})
	return err
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
	if sc.routineComfortPlans {
		capabilities.Methods = append(capabilities.Methods, policy.EnsureComfort)
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
	if sc.routineResearchTarget != "" {
		thresholds.ResearchTarget = sc.routineResearchTarget
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
