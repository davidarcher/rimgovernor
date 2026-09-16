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
	return httpapi.RoutineStatus{
		ReviewsEnabled:  s.reviewsEnabled,
		MethodsEnabled:  s.methodsEnabled,
		ActiveFamilies:  s.families,
		LastReviewTick:  review.Tick,
		LastReviewKnown: review.Revision != 0,
	}, nil
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
		// time EvaluateClockWindow admits a window. On a real, populated map
		// the routine reviewer's full colony census plus any chained
		// planner's native reads can alone take 5s+ (see issue #45), so a 5s
		// MaxAge routinely refused with stale_facts before the write was
		// ever attempted. Not tied to ClockWorkerConfig.CallTimeout's
		// lease/4 ceiling (serve_building.go) -- validated up to 1 minute.
		Profile: profile, MaxAge: 10 * time.Second,
		Start: bridge.ClockStart{Speed: speed, LeaseMS: 30000, MaxTicks: 600,
			Policy: &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(),
				HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.5),
				HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}},
	}
}

// Session owns the attached worker's drain, including failed startup cleanup.
// Starting these loops does not enable Player or acquire native authority.
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, journal *store.Store, sc serveConfig, timeout time.Duration) error {
	profile, clockSpeed, routine, projectLimit := sc.profile, sc.clockSpeed, sc.routineReviews, sc.routineProjectLimit
	sleeping, cooking, shelter, comfort, expansion, power, temperature := sc.routineSleepingPlans, sc.routineCookingPlans, sc.routineShelterPlans, sc.routineComfortPlans, sc.routineExpansionPlans, sc.routinePowerPlans, sc.routineTemperaturePlans
	supplies, work, acquisition, defense, tend, rescue, equip := sc.routineSupplyPlans, sc.routineWorkPlans, sc.routineAcquisitionPlans, sc.routineDefensePlans, sc.routineTendPlans, sc.routineRescuePlans, sc.routineEquipPlans
	secureSupplies, repair, clean, gear, medical, foodStorageUpkeep := sc.routineSecureSuppliesPlans, sc.routineRepairPlans, sc.routineCleanPlans, sc.routineGearPlans, sc.routineMedicalPlans, sc.routineFoodStorageUpkeepPlans
	animalContainment, recovery, husbandry, homeCoverage := sc.routineAnimalContainmentPlans, sc.routineRecoveryPlans, sc.routineHusbandryPlans, sc.routineHomeCoveragePlans
	caravanJourneyTracking, researchTarget, resourceTargets := sc.caravanJourneyTracking, sc.routineResearchTarget, sc.routineResourceTargets.Map()
	allowSlaughter, herdPopulationMax := sc.routineAllowSlaughter, sc.routineHerdPopulationMax.Map()
	animalFeedPlans, productionPolicyPlans, productionReserves, productionStopped := sc.routineAnimalFeedPlans, sc.routineProductionPolicyPlans, sc.routineResourceReserves.Map(), sc.routineStoppedResources.Slice()
	fields, bills, foodStorage := sc.routineFieldPlans, sc.routineBillPlans, sc.routineFoodStoragePlans
	prisonerInteraction, populationCustody, stoneShell := sc.routinePrisonerInteractionPlans, sc.routinePopulationCustodyPlans, sc.routineStoneShellPlans
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
	if (bills || fields || foodStorage || acquisition || work || supplies || sleeping || cooking || shelter || comfort || expansion || power || temperature || defense || tend || rescue || equip || secureSupplies || repair || clean || haul || waste || moodRelief || gear || medical || foodStorageUpkeep || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || homeCoverage || stoneShell || naming || researchTarget != "" || len(resourceTargets) > 0 || animalFeedPlans || productionPolicyPlans) && !routine {
		return errors.New("building plans require routine reviews")
	}
	if routine {
		native, ok := reads.(observation.RoutineSource)
		if !ok {
			return errors.New("routine reviews require typed colony and emergency observations")
		}
		thresholds := policy.DefaultRoutinePolicy()
		thresholds.MaxDevelopmentProjects = projectLimit
		capabilities := buildingruntime.RoutineCapabilities{}
		if acquisition || fields || bills {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureFoodSupply)
		}
		if foodStorage {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureFoodStorage)
		}
		if acquisition {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainWood)
		}
		if bills {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureCooking)
		}
		if temperature {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureTemperatureSafety)
		}
		if power {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureBasicPower)
		}
		if comfort {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureComfort)
		}
		if expansion {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureExpansion)
		}
		if animalContainment {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainAnimalContainment)
		}
		if repair {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainEssentialRepairs)
		}
		if clean {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainCleanFacilities)
		}
		if haul {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainStorage)
		}
		if waste {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainWaste)
		}
		if recovery {
			capabilities.Methods = append(capabilities.Methods, policy.RecoverDisasterServices)
		}
		if husbandry {
			thresholds.AllowSlaughter = allowSlaughter
			thresholds.HerdPopulationMax = herdPopulationMax
			capabilities.Methods = append(capabilities.Methods, policy.MaintainHerd)
		}
		if prisonerInteraction || populationCustody {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainPopulation)
		}
		if homeCoverage {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainHomeCoverage)
		}
		if stoneShell {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainStoneShell)
		}
		if researchTarget != "" {
			thresholds.ResearchTarget = researchTarget
			capabilities.Methods = append(capabilities.Methods, policy.EnsureResearch)
		}
		if len(resourceTargets) > 0 {
			thresholds.ResourceTargets = resourceTargets
			capabilities.Methods = append(capabilities.Methods, policy.MaintainResource)
		}
		if animalFeedPlans {
			capabilities.Methods = append(capabilities.Methods, policy.MaintainAnimalFeed)
		}
		if productionPolicyPlans {
			thresholds.ResourceReserves = productionReserves
			thresholds.StoppedResources = productionStopped
			capabilities.Methods = append(capabilities.Methods, policy.ProductionPolicy)
		}
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
		if sleeping || cooking || shelter || comfort || expansion || power || temperature {
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
		}
	}
	scheduler, err := buildingruntime.NewClockScheduler(player, session, reads, config, wallClock{})
	if err != nil {
		return err
	}
	_, err = buildingruntime.NewClockWorker(ctx, scheduler, reads, buildingruntime.ClockWorkerConfig{
		PollInterval: time.Second, RenewInterval: 5 * time.Second, StepInterval: time.Second,
		MaxBackoff: 10 * time.Second, CallTimeout: timeout, PageLimit: 128,
	})
	return err
}
