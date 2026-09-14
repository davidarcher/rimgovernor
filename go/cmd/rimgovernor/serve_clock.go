package main

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
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

type serviceClockReads interface {
	buildingruntime.ClockWindowNative
	buildingruntime.ClockEventNative
}

func serviceClockConfig(profile string) buildingruntime.ClockSchedulerConfig {
	return buildingruntime.ClockSchedulerConfig{
		Profile: profile, MaxAge: 5 * time.Second,
		Start: bridge.ClockStart{Speed: k.Speed_SPEED_NORMAL, LeaseMS: 30000, MaxTicks: 600,
			Policy: &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(),
				HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.5),
				HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}},
	}
}

// Session owns the attached worker's drain, including failed startup cleanup.
// Starting these loops does not enable Player or acquire native authority.
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, journal *store.Store, profile string, timeout time.Duration, routine, sleeping, cooking, shelter, comfort, expansion, power, temperature bool, projectLimit int, supplies, work, acquisition, defense, tend, rescue, equip, secureSupplies, repair, clean, gear, medical, animalContainment, recovery, husbandry, homeCoverage, caravanJourneyTracking bool, researchTarget string, resourceTargets map[policy.Resource]int64, fieldOptions ...bool) error {
	// fieldOptions carries the field/bill/foodStorage/prisonerInteraction/
	// populationCustody/stoneShell flags, in that fixed order, appended by the caller.
	config := serviceClockConfig(profile)
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
	fields := len(fieldOptions) >= 1 && fieldOptions[0]
	bills := len(fieldOptions) >= 2 && fieldOptions[1]
	foodStorage := len(fieldOptions) >= 3 && fieldOptions[2]
	prisonerInteraction := len(fieldOptions) >= 4 && fieldOptions[3]
	populationCustody := len(fieldOptions) >= 5 && fieldOptions[4]
	stoneShell := len(fieldOptions) == 6 && fieldOptions[5]
	if len(fieldOptions) > 6 {
		return errors.New("invalid field option")
	}
	if (bills || fields || foodStorage || acquisition || work || supplies || sleeping || cooking || shelter || comfort || expansion || power || temperature || defense || tend || rescue || equip || secureSupplies || repair || clean || gear || medical || animalContainment || recovery || husbandry || prisonerInteraction || populationCustody || homeCoverage || stoneShell || researchTarget != "" || len(resourceTargets) > 0) && !routine {
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
		if recovery {
			capabilities.Methods = append(capabilities.Methods, policy.RecoverDisasterServices)
		}
		if husbandry {
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
			if shelter {
				config.Sleeping, err = buildingruntime.NewRoutineShelterPlanner(reviewer, source)
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
				config.Expansion, err = buildingruntime.NewRoutineExpansionPlanner(reviewer, source)
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
