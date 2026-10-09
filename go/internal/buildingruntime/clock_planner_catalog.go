package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// plannerEntry declares a queued planner's priority, dependencies and wake conditions.
type plannerEntry struct {
	name string
	// concern receives this planner's refusal. Multi-concern planners leave it empty.
	concern policy.ConcernID
	// class says whether the admission cycle waits on the planner:
	// critical for the preempt and critical priority classes and the
	// emergency evidence (fire safety), optional for the development
	// reviews. TestPlannerCatalogClasses holds the rule.
	class    plannerClass
	priority int
	// startup keeps shelter planning in the critical wave while NeedsShelter
	// holds development. Optional-wave grace must not discard this work.
	startup bool
	// kinds are the action kinds the planner dispatches. A terminal outcome
	// of one of these kinds is the planner's own work completing, so a wake
	// carrying it re-runs the planner (and only planners of that kind).
	kinds []domain.ActionKind
	// sections are the review census sections the planner consumes:
	// a wake whose invalidations dirty one re-runs the planner,
	// and a step that selects a subset reads only what its planners
	// declare (the rest is served held). families names the sectionless
	// families the planner plans from (world); see factFamilies.
	sections []facts.Section
	families []bridge.FactFamily
	// every is the planner's review cadence in game ticks, zero for its
	// priority class's default (reviewEvery).
	every domain.Tick
	// configured reports whether the composition wired this planner.
	configured func(*ClockSchedulerConfig) bool
	// run steps the planner and stores its result on out; the error is
	// wrapped with the planner's name.
	run func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error)
}

// Census dependencies let each planner read only its required sections.
// Building planners need spatial facts as well as the colony summary.
var (
	sectionsBuilding = []facts.Section{facts.Colony, facts.PlanningCells, facts.Zones, facts.Buildings, facts.Rooms}
	// sectionsThrone adds the colonists the throne light refuel picks a
	// hauler from.
	sectionsThrone = []facts.Section{facts.Colony, facts.PlanningCells, facts.Zones, facts.Buildings, facts.Rooms, facts.Pawns}
	sectionsColony = []facts.Section{facts.Colony}
	// sectionsRituals: the ritual plan reads the ideoligion, the building
	// sites, the colonists and the emergency census.
	sectionsRituals = []facts.Section{facts.Colony, facts.Pawns, facts.Emergency, facts.Buildings, facts.Ideology}
	sectionsBills   = []facts.Section{facts.Colony, facts.Bills}
	sectionsZones   = []facts.Section{facts.Colony, facts.Zones}
	sectionsPawns   = []facts.Section{facts.Pawns}
	sectionsMedical = []facts.Section{facts.Pawns, facts.Colony}
	sectionsThreat  = []facts.Section{facts.Pawns, facts.Emergency}
	// sectionsWork adds the population census for the owned-pawn names
	// and the colony facts for the reading, drug and
	// food policies.
	sectionsWork     = []facts.Section{facts.Pawns, facts.Population, facts.Emergency, facts.Colony, facts.Buildings}
	sectionsRecovery = []facts.Section{facts.Pawns, facts.Emergency, facts.Colony}
	sectionsCustody  = []facts.Section{facts.Pawns, facts.Population, facts.Emergency, facts.Colony}
	sectionsResearch = []facts.Section{facts.Research, facts.Colony, facts.Buildings, facts.Rooms}
	sectionsFire     = []facts.Section{facts.Emergency, facts.Colony}
	sectionsResource = []facts.Section{facts.Colony, facts.Bills, facts.Buildings, facts.Zones}
	// sectionsArmory is the bills and threat sets plus the research census
	// the armory tier is capped by.
	sectionsArmory = []facts.Section{facts.Colony, facts.Bills, facts.Pawns, facts.Emergency, facts.Research}
)

// The sectionless families planners declare beside their sections
// (plannerEntry.families): the emergency status read for a planner whose
// census sections do not carry it, and the world read of the caravan trade.
var (
	familiesEmergency = []bridge.FactFamily{bridge.FactEmergency}
	familiesWorld     = []bridge.FactFamily{bridge.FactWorld}
)

// Review cadences, in game ticks (2500 an hour): how long a planner's last
// evaluation stands before the due queue re-runs it without evidence
// (plannerEntry.every). Evidence (an outcome of its kinds, a dirty
// section it declares) re-runs it sooner.
const (
	reviewEveryUrgent  domain.Tick = domain.TicksPerHour
	reviewEveryRounds  domain.Tick = domain.TicksPerHour
	reviewEveryComfort domain.Tick = 15000
)

// reviewEvery is the planner's cadence: its own when set, else its
// priority class's.
func (e plannerEntry) reviewEvery() domain.Tick {
	if e.every > 0 {
		return e.every
	}
	switch e.priority {
	case plannerMaintenance:
		return reviewEveryRounds
	case plannerComfort:
		return reviewEveryComfort
	}
	return reviewEveryUrgent
}

// plannerCatalog lists every routine planner in queue order. Priorities
// follow policy.DetectRounds's class for the planner's goal; queue order
// breaks priority ties (plannerGroup.Wait is stable), so the order here is
// the order Step queued them inline.
var plannerCatalog = []plannerEntry{
	{name: "undraft", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.OwnedDraftAction}, sections: sectionsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Rounds != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			return Verdict{}, s.config.Rounds.sweepDrafts(ctx, epoch, arbiter)
		}},
	{name: "work", concern: policy.EnsureWorkAssignments, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.WorkAssignmentAction, domain.PawnSettingsAction, domain.ReadingPolicyAction, domain.DrugPolicyAction, domain.FoodPolicyAction, domain.BuildingAction}, sections: sectionsWork,
		configured: func(c *ClockSchedulerConfig) bool { return c.Work != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Work.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Work = &method
			return method.Verdict, nil
		}},
	{name: "fields", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.ZoneCreateAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Fields != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Fields.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Fields = &method
			return method.Verdict, nil
		}},
	{name: "foodAcquisition", concern: policy.EnsureFoodSupply, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.AcquisitionWithdrawAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.FoodAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.FoodAcquisition = &method
			return method.Verdict, nil
		}},
	{name: "pestAcquisition", concern: policy.ClearPests, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.AcquisitionWithdrawAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.PestAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PestAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.PestAcquisition = &method
			return method.Verdict, nil
		}},
	{name: "resourceAcquisition", concern: policy.MaintainResource, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.AcquisitionWithdrawAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.ResourceAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.ResourceAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.ResourceAcquisition = &method
			return method.Verdict, nil
		}},
	{name: "supplies", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.SupplyAllowAction, domain.SupplyForbidAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Supplies != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Supplies.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Supplies = &method
			return method.Verdict, nil
		}},
	{name: "sleeping", concern: policy.MaintainHousing, class: classOptional, startup: true, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.DeconstructionAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Sleeping != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Sleeping.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Sleeping = &method
			return method.Verdict, nil
		}},
	{name: "power", concern: policy.EnsureBasicPower, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Power != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Power.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Power = &method
			return method.Verdict, nil
		}},
	{name: "temperature", concern: policy.EnsureTemperatureSafety, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.DoorControlAction, domain.OwnedDraftAction, domain.MovementAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Temperature != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Temperature.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Temperature = &method
			return method.Verdict, nil
		}},
	{name: "refrigeration", concern: policy.MaintainRefrigeration, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.BuildingTemperatureAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Refrigeration != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Refrigeration.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Refrigeration = &method
			return method.Verdict, nil
		}},
	{name: "lighting", concern: policy.MaintainLighting, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Lighting != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Lighting.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Lighting = &method
			return method.Verdict, nil
		}},
	{name: "flooring", concern: policy.MaintainFlooring, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Flooring != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Flooring.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Flooring = &method
			return method.Verdict, nil
		}},
	{name: "routes", concern: policy.MaintainRoutes, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.DoorControlAction, domain.OwnedDraftAction, domain.MovementAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Routes != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Routes.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Routes = &method
			return method.Verdict, nil
		}},
	{name: "cooking", concern: policy.EnsureCooking, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Cooking != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Cooking.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Cooking = &method
			return method.Verdict, nil
		}},
	{name: "butcher", concern: policy.MaintainButcherSpot, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Butcher != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Butcher.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Butcher = &method
			return method.Verdict, nil
		}},
	{name: "cookingBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.CookingBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.CookingBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.CookingBills = &method
			return method.Verdict, nil
		}},
	{name: "preservationBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.PreservationBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PreservationBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.PreservationBills = &method
			return method.Verdict, nil
		}},
	{name: "butcherBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction, domain.ZoneCreateAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.ButcherBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.ButcherBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.ButcherBills = &method
			return method.Verdict, nil
		}},
	{name: "cookAheadBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.CookAheadBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.CookAheadBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.CookAheadBills = &method
			return method.Verdict, nil
		}},
	{name: "artBills", concern: policy.MaintainArt, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction, domain.RemoveProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.ArtBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.ArtBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.ArtBills = &method
			return method.Verdict, nil
		}},
	{name: "babyFoodBills", concern: policy.MaintainBabyFeeding, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.BabyFoodBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.BabyFoodBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.BabyFoodBills = &method
			return method.Verdict, nil
		}},
	{name: "surgeryPartBills", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction, domain.RemoveProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.SurgeryPartBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.SurgeryPartBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.SurgeryPartBills = &method
			return method.Verdict, nil
		}},
	{name: "mechBills", concern: policy.MaintainMechs, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.MechBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.MechBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.MechBills = &method
			return method.Verdict, nil
		}},
	{name: "basicComfort", concern: policy.EnsureComfort, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.BasicComfort != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.BasicComfort.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.BasicComfort = &method
			return method.Verdict, nil
		}},
	{name: "comfort", concern: policy.EnsureComfort, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Comfort != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Comfort.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Comfort = &method
			return method.Verdict, nil
		}},
	{name: "workshop", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Workshop != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Workshop.stepWorkshops(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Workshop = &method
			return method.Verdict, nil
		}},
	{name: "hospital", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.BuildingAction, domain.BedUseAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Hospital != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Hospital.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Hospital = &method
			return method.Verdict, nil
		}},
	{name: "sleepingUpkeep", concern: policy.MaintainHousing, class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.BuildingAction, domain.AssignAction, domain.MoveBuildingAction, domain.UninstallBuildingAction, domain.RecoveryServiceAction}, sections: sectionsThrone,
		configured: func(c *ClockSchedulerConfig) bool { return c.SleepingUpkeep != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.SleepingUpkeep.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.SleepingUpkeep = &method
			return method.Verdict, nil
		}},
	{name: "expansion", concern: policy.MaintainHousing, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.ExcavationAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Expansion != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Expansion.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Expansion = &method
			return method.Verdict, nil
		}},
	{name: defensePlanner, class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.OwnedDraftAction, domain.SubdueAction, domain.MovementAction, domain.AbilityAction}, sections: sectionsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Defense != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Defense.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Defense = &method
			return method.Verdict, nil
		}},
	{name: "medical", concern: policy.MaintainMedicalReserves, class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.ProductionBillAction, domain.WorkAssignmentAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Medical != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Medical.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Medical = &method
			return method.Verdict, nil
		}},
	{name: "surgery", concern: policy.MaintainSurgery, class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.SurgeryAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Surgery != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Surgery.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Surgery = &method
			return method.Verdict, nil
		}},
	{name: "tend", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.TendAction}, sections: sectionsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Tend != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Tend.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Tend = &method
			return method.Verdict, nil
		}},
	{name: "rescue", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.RescueAction}, sections: sectionsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Rescue != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Rescue.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Rescue = &method
			return method.Verdict, nil
		}},
	{name: "equip", concern: policy.MaintainEquipment, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.EquipAction}, sections: sectionsMedical, families: familiesEmergency,
		configured: func(c *ClockSchedulerConfig) bool { return c.Equip != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Equip.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Equip = &method
			return method.Verdict, nil
		}},
	{name: "repair", concern: policy.MaintainEssentialRepairs, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RepairAction}, sections: sectionsBuilding, families: familiesEmergency,
		configured: func(c *ClockSchedulerConfig) bool { return c.Repair != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Repair.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Repair = &method
			return method.Verdict, nil
		}},
	{name: "fireSafety", concern: policy.MaintainFireSafety, class: classCritical, priority: plannerFoothold, sections: sectionsFire,
		configured: func(c *ClockSchedulerConfig) bool { return c.FireSafety != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, _ *stepArbiter) (Verdict, error) {
			method, err := s.config.FireSafety.step(ctx, epoch)
			if err != nil {
				return Verdict{}, err
			}
			out.FireSafety = &method
			return method.Verdict, nil
		}},
	{name: "clearance", concern: policy.ClearHomeObstructions, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.DeconstructionAction, domain.ZoneCreateAction, domain.CoverClearanceAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clearance != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Clearance.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Clearance = &method
			return method.Verdict, nil
		}},
	{name: "shrine", concern: policy.ClearAncientShrine, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.OwnedDraftAction, domain.MovementAction, domain.DeconstructionAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Shrine != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Shrine.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Shrine = &method
			return method.Verdict, nil
		}},
	{name: "clean", concern: policy.MaintainCleanFacilities, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CleanAction}, sections: sectionsBuilding, families: familiesEmergency,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clean != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Clean.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Clean = &method
			return method.Verdict, nil
		}},
	{name: "pollution", concern: policy.ManagePollution, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AreaAction, domain.SupplyAllowAction, domain.WastepackHaulAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Pollution != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Pollution.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Pollution = &method
			return method.Verdict, nil
		}},
	{name: "mechcharger", concern: policy.EnsureMechCharger, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.MechCharger != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.MechCharger.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.MechCharger = &method
			return method.Verdict, nil
		}},
	{name: "genebank", concern: policy.MaintainGeneBank, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.GeneBank != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.GeneBank.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.GeneBank = &method
			return method.Verdict, nil
		}},
	{name: "workLedger", concern: policy.MaintainWorkLedger, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction, domain.RemoveProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.Ledger != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Ledger.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Ledger = &method
			return method.Verdict, nil
		}},
	{name: "blight", concern: policy.RemoveBlight, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CutPlantAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Blight != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Blight.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Blight = &method
			return method.Verdict, nil
		}},
	{name: "armory", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction, domain.RemoveProductionBillAction, domain.BuildingAction}, sections: sectionsArmory,
		configured: func(c *ClockSchedulerConfig) bool { return c.Armory != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Armory.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Armory = &method
			return method.Verdict, nil
		}},
	{name: "burial", concern: policy.MaintainBurial, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Burial != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Burial.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Burial = &method
			return method.Verdict, nil
		}},
	{name: "training", concern: policy.MaintainTraining, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Training != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Training.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Training = &method
			return method.Verdict, nil
		}},
	{name: "incineration", concern: policy.MaintainIncineration, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.EquipAction, domain.OwnedDraftAction, domain.IgniteAction, domain.CleanAction}, sections: sectionsBuilding, families: familiesEmergency,
		configured: func(c *ClockSchedulerConfig) bool { return c.Incineration != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Incineration.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Incineration = &method
			return method.Verdict, nil
		}},
	{name: "moodRelief", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MoodReliefAction, domain.AbilityAction}, sections: sectionsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.MoodRelief != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.MoodRelief.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.MoodRelief = &method
			return method.Verdict, nil
		}},
	{name: "gear", concern: policy.MaintainEquipment, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.GearReplaceAction, domain.ApparelPolicyAction, domain.ProductionBillAction, domain.RemoveProductionBillAction, domain.PolicyPruneAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Gear != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Gear.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Gear = &method
			return method.Verdict, nil
		}},
	{name: "foodStorageUpkeep", concern: policy.MaintainFoodStorage, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.HaulAction, domain.ProductionBillAction, domain.SupplyAllowAction, domain.SupplyForbidAction, domain.ZoneCreateAction}, sections: sectionsZones,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodStorageUpkeep != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.FoodStorageUpkeep.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.FoodStorageUpkeep = &method
			return method.Verdict, nil
		}},
	{name: "animalContainment", concern: policy.MaintainAnimalContainment, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.AnimalContainment != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.AnimalContainment.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.AnimalContainment = &method
			return method.Verdict, nil
		}},
	{name: "recovery", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.RecoveryServiceAction, domain.WorkAssignmentAction, domain.HusbandryAction}, sections: sectionsRecovery,
		configured: func(c *ClockSchedulerConfig) bool { return c.Recovery != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Recovery.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Recovery = &method
			return method.Verdict, nil
		}},
	{name: "rules", concern: policy.EnsureFoodSupply, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RulesAttachAction}, sections: sectionsWork, every: reviewEveryRounds / 2,
		configured: func(c *ClockSchedulerConfig) bool { return c.Rules != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Rules.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Rules = &method
			return method.Verdict, nil
		}},
	{name: "husbandry", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.HusbandryAction}, sections: []facts.Section{facts.Pawns, facts.Colony},
		configured: func(c *ClockSchedulerConfig) bool { return c.Husbandry != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Husbandry.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Husbandry = &method
			return method.Verdict, nil
		}},
	{name: "prisonerInteraction", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.PrisonerInteractionAction}, sections: []facts.Section{facts.Population, facts.Pawns},
		configured: func(c *ClockSchedulerConfig) bool { return c.PrisonerInteraction != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PrisonerInteraction.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.PrisonerInteraction = &method
			return method.Verdict, nil
		}},
	{name: "populationCustody", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.CaptureAction, domain.UseItemAction, domain.RescueAction, domain.OpenCasketAction, domain.TendAction, domain.DoorControlAction, domain.RecoveryServiceAction}, sections: sectionsCustody,
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationCustody != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PopulationCustody.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.PopulationCustody = &method
			return method.Verdict, nil
		}},
	{name: "populationJoiner", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.TendAction, domain.RecoveryServiceAction, domain.QuestAcceptAction, domain.QuestShuttleAction, domain.HackDesignationAction, domain.GiveItemAction, domain.RitualAction, domain.DialogAnswerAction, domain.ProductionBillAction, domain.ZoneCreateAction, domain.AcquisitionAction, domain.MineAcquisitionAction, domain.PrisonerInteractionAction, domain.BuildingAction, domain.MoveBuildingAction, domain.HaulAction, domain.CaravanDepartureAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationJoiner != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PopulationJoiner.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.PopulationJoiner = &method
			return method.Verdict, nil
		}},
	{name: "research", concern: policy.EnsureResearch, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ResearchSelectAction, domain.BuildingAction}, sections: sectionsResearch,
		configured: func(c *ClockSchedulerConfig) bool { return c.Research != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Research.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Research = &method
			return method.Verdict, nil
		}},
	{name: "storage-shelves", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.StorageShelves != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.StorageShelves.step(ctx, epoch)
			if err != nil {
				return Verdict{}, err
			}
			out.StorageShelves = &method
			return method.Verdict, nil
		}},
	{name: "dialog", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.DialogAnswerAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Dialog != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Dialog.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Dialog = &method
			return method.Verdict, nil
		}},
	{name: "trade", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.TradeAction, domain.CommsTradeRequestAction}, sections: sectionsColony, families: familiesWorld,
		configured: func(c *ClockSchedulerConfig) bool { return c.Trade != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Trade.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Trade = &method
			return method.Verdict, nil
		}},
	{name: "resource", concern: policy.MaintainResource, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MineAcquisitionAction, domain.ProductionBillAction, domain.ZoneCreateAction, domain.BuildingAction, domain.DeconstructionAction}, sections: sectionsResource,
		configured: func(c *ClockSchedulerConfig) bool { return c.Resource != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Resource.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Resource = &method
			return method.Verdict, nil
		}},
	{name: "maintainShelter", concern: policy.MaintainShelter, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.AreaAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.MaintainShelter != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.MaintainShelter.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.MaintainShelter = &method
			return method.Verdict, nil
		}},
	{name: "firebreak", concern: policy.MaintainFirebreak, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AreaPlantCutAction, domain.CoverClearanceAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Firebreak != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Firebreak.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Firebreak = &method
			return method.Verdict, nil
		}},
	{name: "psylink", concern: policy.MaintainPsylink, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.UseItemAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Psylink != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Psylink.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Psylink = &method
			return method.Verdict, nil
		}},
	{name: "creepJoiners", concern: policy.ManageCreepJoiners, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.DropEquipmentAction, domain.SurgeryAction, domain.WorkAssignmentAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.CreepJoiners != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.CreepJoiners.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.CreepJoiners = &method
			return method.Verdict, nil
		}},
	{name: "ideoRoles", concern: policy.MaintainIdeoRoles, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AssignAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.IdeoRoles != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.IdeoRoles.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.IdeoRoles = &method
			return method.Verdict, nil
		}},
	{name: "rituals", concern: policy.MaintainRituals, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RitualAction}, sections: sectionsRituals,
		configured: func(c *ClockSchedulerConfig) bool { return c.Rituals != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Rituals.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Rituals = &method
			return method.Verdict, nil
		}},
	{name: "gathering", concern: policy.HoldGatherings, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.GatheringAction}, sections: sectionsRituals,
		configured: func(c *ClockSchedulerConfig) bool { return c.Gathering != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Gathering.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Gathering = &method
			return method.Verdict, nil
		}},
	{name: "ideoligion", concern: policy.ImproveIdeoligion, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.IdeoligionReformAction}, sections: sectionsRituals,
		configured: func(c *ClockSchedulerConfig) bool { return c.Ideoligion != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Ideoligion.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Ideoligion = &method
			return method.Verdict, nil
		}},
	{name: "permits", concern: policy.MaintainPermits, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.PawnSettingsAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Permits != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Permits.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Permits = &method
			return method.Verdict, nil
		}},
	{name: "homeCoverage", concern: policy.MaintainHomeCoverage, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.AreaAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.HomeCoverage != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.HomeCoverage.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.HomeCoverage = &method
			return method.Verdict, nil
		}},
	{name: "stoneShell", concern: policy.MaintainStoneShell, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.WallRemovalAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.StoneShell != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.StoneShell.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.StoneShell = &method
			return method.Verdict, nil
		}},
	{name: "stockpiles", concern: policy.MaintainStockpiles, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ZoneCellEditAction, domain.StockpilePatchAction, domain.ZoneDeleteAction, domain.ZoneCreateAction, domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Stockpiles != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Stockpiles.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Stockpiles = &method
			return method.Verdict, nil
		}},
	{name: "defenseLayout", concern: policy.EnsureDefensiveLayout, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.RecoveryServiceAction, domain.CoverClearanceAction, domain.FoundationRemovalAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.DefenseLayout != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.DefenseLayout.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.DefenseLayout = &method
			return method.Verdict, nil
		}},
}

// queuePlanners queues the configured planners pick selects (all when nil)
// onto the wave, returning the names queued in catalog order. A nil pick
// selects the whole catalog. With startup set, a startup planner is queued
// into the critical cycle rather than the optional wave (plannerEntry.startup).
func (s *ClockScheduler) queuePlanners(ctx, epoch context.Context, wave *plannerWave, arbiter *stepArbiter, pick func(plannerEntry) bool, startup bool) []string {
	var queued []string
	for _, entry := range s.catalog {
		if !entry.configured(&s.config) || pick != nil && !pick(entry) {
			continue
		}
		if startup && entry.startup || immediateReview(ctx) && immediatePlanner(entry) {
			entry.class = classCritical
		}
		queued = append(queued, entry.name)
		wave.queue(s, ctx, epoch, arbiter, entry)
	}
	return queued
}

// declared is the fact families the planner may read: the family of each
// census section it consumes plus its sectionless families. The
// wake step re-runs a planner on exactly these invalidations, so a read
// outside them would plan on facts nothing wakes it for. Two families are
// ambient and declared by no one: definitions (the load's catalog, fixed
// until a new load replaces the whole world) and identity (the world
// identity every planner checks its step against).
func (e plannerEntry) declared() map[bridge.FactFamily]bool {
	out := map[bridge.FactFamily]bool{bridge.FactDefinitions: true, bridge.FactIdentity: true}
	for _, section := range e.sections {
		out[section.Family()] = true
	}
	for _, family := range e.families {
		out[family] = true
	}
	return out
}

// plannerReadAudit is called for every fact family a catalog planner reads
// that its entry does not declare (the recording facts reader,
// bridge.ReadNote). Only the buildingruntime test binary installs one (in
// TestMain, a collector that fails the run); production reads unaudited.
var plannerReadAudit func(planner string, family bridge.FactFamily, source string)

// auditReads returns ctx carrying the planner's read note when an audit is
// installed.
func (e plannerEntry) auditReads(ctx context.Context) context.Context {
	audit := plannerReadAudit
	if audit == nil {
		return ctx
	}
	declared := e.declared()
	return bridge.WithReadNote(ctx, func(family bridge.FactFamily, source string) {
		if !declared[family] {
			audit(e.name, family, source)
		}
	})
}
