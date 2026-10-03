package buildingruntime

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// plannerEntry is one routine planner the clock step can queue. The table
// replaces the inline per-planner blocks Step used to carry: the same set,
// priorities and queue order, now selectable by step reason.
type plannerEntry struct {
	name string
	// goal is the single goal the planner serves: the record its refusal is
	// filed on (GoalProgress.Planner). Planners serving several goals or
	// none leave it empty and file nothing.
	goal policy.GoalID
	// class says whether the admission cycle waits on the planner (#623):
	// critical for the preempt and critical priority classes and the
	// emergency evidence (fire safety), optional for the development
	// reviews. TestPlannerCatalogClasses holds the rule.
	class    plannerClass
	priority int
	// startup promotes the planner into the critical wave while the colony
	// stage holds development (the shelter every other goal waits for is
	// unmet, ColonyStageRecord.HoldsDevelopment). Its work is the hold
	// itself, not a development review, so the one-second optional grace
	// must not discard it: siting a starter shell walks the bunk rungs and
	// previews a ring, seconds of native round trips, and every step
	// dropping it left the goal without a live method for good (#658).
	startup bool
	// kinds are the action kinds the planner dispatches. A terminal outcome
	// of one of these kinds is the planner's own work completing, so a wake
	// carrying it re-runs the planner (and only planners of that kind).
	kinds []domain.ActionKind
	// sections are the review census sections the planner consumes
	// (#625): a wake whose invalidations dirty one re-runs the planner,
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

// The section sets planners consume from the review's census
// (snapshot frame sections, facts.Section). Of the seven family
// lists the catalog used to carry, only the building planners' needed the
// whole colony read (the colony facts, the planning cells, the entity
// sections and the room census); every other colony reader consumes the
// colony facts section alone, or that plus its own entity section.
var (
	sectionsBuilding = []facts.Section{facts.Colony, facts.PlanningCells, facts.Zones, facts.Buildings, facts.Rooms}
	sectionsColony   = []facts.Section{facts.Colony}
	// sectionsRituals: the ritual plan reads the ideoligion, the building
	// sites, the colonists and the emergency census (#1660).
	sectionsRituals = []facts.Section{facts.Colony, facts.Pawns, facts.Emergency, facts.Buildings, facts.Ideology}
	sectionsBills   = []facts.Section{facts.Colony, facts.Bills}
	sectionsZones   = []facts.Section{facts.Colony, facts.Zones}
	sectionsPawns   = []facts.Section{facts.Pawns}
	sectionsMedical = []facts.Section{facts.Pawns, facts.Colony}
	sectionsThreat  = []facts.Section{facts.Pawns, facts.Emergency}
	// sectionsWork adds the population census for the owned-pawn names
	// (#1310) and the colony facts for the reading (#1306), drug (#1537) and
	// food (#1541) policies.
	sectionsWork     = []facts.Section{facts.Pawns, facts.Population, facts.Emergency, facts.Colony}
	sectionsRecovery = []facts.Section{facts.Pawns, facts.Emergency, facts.Colony}
	sectionsCustody  = []facts.Section{facts.Pawns, facts.Population, facts.Emergency, facts.Colony}
	sectionsResearch = []facts.Section{facts.Research, facts.Colony, facts.Buildings, facts.Rooms}
	sectionsFire     = []facts.Section{facts.Emergency, facts.Colony}
	sectionsResource = []facts.Section{facts.Colony, facts.Bills, facts.Buildings, facts.Zones}
	// sectionsArmory is the bills and threat sets plus the research census
	// the armory tier is capped by (#1201).
	sectionsArmory = []facts.Section{facts.Colony, facts.Bills, facts.Pawns, facts.Emergency, facts.Research}
)

// factsBuilding and factsColony are the fact families of the building and
// colony section sets: what a migrated planner names on its Proposal
// (#622).
var (
	factsBuilding = sectionFamilies(sectionsBuilding)
	factsColony   = sectionFamilies(sectionsColony)
)

// sectionFamilies lists the families of sections, each once, in order.
func sectionFamilies(sections []facts.Section) []bridge.FactFamily {
	var out []bridge.FactFamily
	seen := map[bridge.FactFamily]bool{}
	for _, section := range sections {
		if family := section.Family(); !seen[family] {
			seen[family] = true
			out = append(out, family)
		}
	}
	return out
}

// Review cadences, in game ticks (2500 an hour): how long a planner's last
// evaluation stands before the due queue re-runs it without evidence
// (plannerEntry.every, #625). Evidence (an outcome of its kinds, a dirty
// section it declares) re-runs it sooner.
const (
	reviewEveryUrgent  domain.Tick = domain.TicksPerHour
	reviewEveryRoutine domain.Tick = domain.TicksPerHour
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
		return reviewEveryRoutine
	case plannerComfort:
		return reviewEveryComfort
	}
	return reviewEveryUrgent
}

// plannerCatalog lists every routine planner in queue order. Priorities
// follow policy.DetectRoutine's class for the planner's goal; queue order
// breaks priority ties (plannerGroup.Wait is stable), so the order here is
// the order Step queued them inline.
var plannerCatalog = []plannerEntry{
	{name: "undraft", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.OwnedDraftAction}, sections: sectionsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Routine != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			return Verdict{}, s.config.Routine.sweepDrafts(ctx, epoch, arbiter)
		}},
	{name: "work", goal: policy.EnsureWorkAssignments, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.WorkAssignmentAction, domain.PawnSettingsAction, domain.ReadingPolicyAction, domain.DrugPolicyAction, domain.FoodPolicyAction}, sections: sectionsWork,
		configured: func(c *ClockSchedulerConfig) bool { return c.Work != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Work.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Work.step result: reason=%v plan=%v", method.Verdict, method.Plan)
			out.Work = &method
			return method.Verdict, nil
		}},
	{name: "fields", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.ZoneCreateAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Fields != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			started := time.Now()
			method, err := s.config.Fields.step(ctx, epoch, arbiter)
			if err != nil {
				clockSchedulerLog("Fields.step failed after %s: %v", time.Since(started), err)
				return Verdict{}, err
			}
			clockSchedulerLog("Fields.step result: reason=%v plan=%s wait=%d", method.Verdict, method.Plan, method.NativeWorkTicks)
			out.Fields = &method
			return method.Verdict, nil
		}},
	{name: "foodAcquisition", goal: policy.EnsureFoodSupply, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.AcquisitionWithdrawAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.FoodAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("FoodAcquisition.step result: reason=%v plan=%s", method.Verdict, method.Plan)
			out.FoodAcquisition = &method
			return method.Verdict, nil
		}},
	{name: "pestAcquisition", goal: policy.ClearPests, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.AcquisitionWithdrawAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.PestAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PestAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("PestAcquisition.step result: reason=%v plan=%s", method.Verdict, method.Plan)
			out.PestAcquisition = &method
			return method.Verdict, nil
		}},
	{name: "resourceAcquisition", goal: policy.MaintainResource, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.AcquisitionWithdrawAction}, sections: sectionsColony,
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
	{name: "sleeping", goal: policy.MaintainHousing, class: classOptional, startup: true, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.DeconstructionAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Sleeping != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Sleeping.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Sleeping.step result: reason=%v admitted=%v refused=%v", method.Verdict, method.Decision.Admitted, method.Decision.Refused)
			out.Sleeping = &method
			return method.Verdict, nil
		}},
	{name: "power", goal: policy.EnsureBasicPower, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Power != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Power.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Power.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Verdict, method.Decision, method.NativeWorkTicks)
			out.Power = &method
			return method.Verdict, nil
		}},
	{name: "temperature", goal: policy.EnsureTemperatureSafety, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Temperature != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Temperature.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Temperature.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Verdict, method.Decision, method.NativeWorkTicks)
			out.Temperature = &method
			return method.Verdict, nil
		}},
	{name: "refrigeration", goal: policy.MaintainRefrigeration, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.BuildingTemperatureAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Refrigeration != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Refrigeration.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Refrigeration.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Verdict, method.Decision, method.NativeWorkTicks)
			out.Refrigeration = &method
			return method.Verdict, nil
		}},
	{name: "lighting", goal: policy.MaintainLighting, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Lighting != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Lighting.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Lighting.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Verdict, method.Decision, method.NativeWorkTicks)
			out.Lighting = &method
			return method.Verdict, nil
		}},
	{name: "flooring", goal: policy.MaintainFlooring, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Flooring != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Flooring.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Flooring.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Verdict, method.Decision, method.NativeWorkTicks)
			out.Flooring = &method
			return method.Verdict, nil
		}},
	{name: "routes", goal: policy.MaintainRoutes, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Routes != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Routes.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Routes.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Verdict, method.Decision, method.NativeWorkTicks)
			out.Routes = &method
			return method.Verdict, nil
		}},
	{name: "cooking", goal: policy.EnsureCooking, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Cooking != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Cooking.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Cooking.step result: reason=%v admitted=%v refused=%v", method.Verdict, method.Decision.Admitted, method.Decision.Refused)
			out.Cooking = &method
			return method.Verdict, nil
		}},
	{name: "butcher", goal: policy.MaintainButcherSpot, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Butcher != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Butcher.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Butcher.step result: reason=%v admitted=%v refused=%v", method.Verdict, method.Decision.Admitted, method.Decision.Refused)
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
			clockSchedulerLog("CookingBills.step result: reason=%v plan=%v", method.Verdict, method.Plan)
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
			clockSchedulerLog("PreservationBills.step result: reason=%v plan=%v", method.Verdict, method.Plan)
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
	{name: "artBills", goal: policy.MaintainArt, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.ArtBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.ArtBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.ArtBills = &method
			return method.Verdict, nil
		}},
	{name: "babyFoodBills", goal: policy.MaintainBabyFeeding, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.BabyFoodBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.BabyFoodBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.BabyFoodBills = &method
			return method.Verdict, nil
		}},
	{name: "surgeryPartBills", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.SurgeryPartBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.SurgeryPartBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.SurgeryPartBills = &method
			return method.Verdict, nil
		}},
	{name: "mechBills", goal: policy.MaintainMechs, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction}, sections: sectionsBills,
		configured: func(c *ClockSchedulerConfig) bool { return c.MechBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.MechBills.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.MechBills = &method
			return method.Verdict, nil
		}},
	{name: "basicComfort", goal: policy.EnsureComfort, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.BasicComfort != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.BasicComfort.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("BasicComfort.step result: reason=%v admitted=%v refused=%v", method.Verdict, method.Decision.Admitted, method.Decision.Refused)
			out.BasicComfort = &method
			return method.Verdict, nil
		}},
	{name: "comfort", goal: policy.EnsureComfort, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
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
			clockSchedulerLog("Workshop.step result: reason=%v admitted=%v refused=%v", method.Verdict, method.Decision.Admitted, method.Decision.Refused)
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
			clockSchedulerLog("Hospital.step result: reason=%v admitted=%v refused=%v", method.Verdict, method.Decision.Admitted, method.Decision.Refused)
			out.Hospital = &method
			return method.Verdict, nil
		}},
	{name: "sleepingUpkeep", goal: policy.MaintainHousing, class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.BuildingAction, domain.AssignAction, domain.MoveBuildingAction, domain.UninstallBuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.SleepingUpkeep != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.SleepingUpkeep.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("SleepingUpkeep.step result: reason=%v admitted=%v refused=%v ticks=%d", method.Verdict, method.Decision.Admitted, method.Decision.Refused, method.NativeWorkTicks)
			out.SleepingUpkeep = &method
			return method.Verdict, nil
		}},
	{name: "expansion", goal: policy.MaintainHousing, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.ExcavationAction}, sections: sectionsBuilding,
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
			clockSchedulerLog("Defense.step result: reason=%v plan=%v", method.Verdict, method.Plan)
			out.Defense = &method
			return method.Verdict, nil
		}},
	{name: "medical", goal: policy.MaintainMedicalReserves, class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.ProductionBillAction, domain.WorkAssignmentAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Medical != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Medical.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Medical.step result: reason=%v plan=%s", method.Verdict, method.Plan)
			out.Medical = &method
			return method.Verdict, nil
		}},
	{name: "surgery", goal: policy.MaintainSurgery, class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.SurgeryAction}, sections: sectionsMedical,
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
	{name: "equip", goal: policy.MaintainEquipment, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.EquipAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Equip != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Equip.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Equip = &method
			return method.Verdict, nil
		}},
	{name: "secureSupplies", goal: policy.SecureSupplies, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.HaulAction, domain.ZoneCreateAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.SecureSupplies != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			// Migrated (#622): the planner proposes; the coordinator after
			// the wave ranks and commits, and its outcome carries the reason
			// the due queue reads (plannerWave.finished, #625).
			result, err := s.config.SecureSupplies.propose(ctx, epoch)
			if err != nil {
				return Verdict{}, err
			}
			arbiter.propose("secureSupplies", result, func(outcome ProposalOutcome) {
				out.SecureSupplies = &RoutineSecureSuppliesResult{Verdict: outcome.Verdict, Plan: outcome.Plan, NativeWorkTicks: uint32(result.NativeWorkTicks)}
			})
			return Verdict{}, nil
		}},
	{name: "repair", goal: policy.MaintainEssentialRepairs, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RepairAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Repair != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Repair.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Repair = &method
			return method.Verdict, nil
		}},
	{name: "fireSafety", goal: policy.MaintainFireSafety, class: classCritical, priority: plannerFoothold, sections: sectionsFire,
		configured: func(c *ClockSchedulerConfig) bool { return c.FireSafety != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, _ *stepArbiter) (Verdict, error) {
			method, err := s.config.FireSafety.step(ctx, epoch)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("FireSafety.step result: reason=%v outcome=%v nativeWorkTicks=%d", method.Verdict, method.Outcome, method.NativeWorkTicks)
			out.FireSafety = &method
			return method.Verdict, nil
		}},
	{name: "clearance", goal: policy.ClearHomeObstructions, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.DeconstructionAction, domain.ZoneCreateAction, domain.CoverClearanceAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clearance != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Clearance.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Clearance = &method
			return method.Verdict, nil
		}},
	{name: "shrine", goal: policy.ClearAncientShrine, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.OwnedDraftAction, domain.MovementAction, domain.DeconstructionAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Shrine != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Shrine.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Shrine = &method
			return method.Verdict, nil
		}},
	{name: "clean", goal: policy.MaintainCleanFacilities, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CleanAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clean != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Clean.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Clean = &method
			return method.Verdict, nil
		}},
	{name: "pollution", goal: policy.ManagePollution, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AreaAction, domain.SupplyAllowAction, domain.WastepackHaulAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Pollution != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Pollution.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Pollution = &method
			return method.Verdict, nil
		}},
	{name: "mechcharger", goal: policy.EnsureMechCharger, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.MechCharger != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.MechCharger.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.MechCharger = &method
			return method.Verdict, nil
		}},
	{name: "blight", goal: policy.RemoveBlight, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CutPlantAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Blight != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Blight.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Blight = &method
			return method.Verdict, nil
		}},
	{name: "armory", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction, domain.BuildingAction}, sections: sectionsArmory,
		configured: func(c *ClockSchedulerConfig) bool { return c.Armory != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Armory.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Armory = &method
			return method.Verdict, nil
		}},
	{name: "waste", goal: policy.MaintainWaste, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.WasteAction, domain.BuildingAction, domain.ProductionBillAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Waste != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Waste.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Waste = &method
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
	{name: "haul", goal: policy.MaintainStorage, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.HaulAction}, sections: sectionsZones,
		configured: func(c *ClockSchedulerConfig) bool { return c.Haul != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			// Migrated (#622): the planner proposes; the coordinator after
			// the wave ranks and commits, and its outcome carries the reason
			// the due queue reads (plannerWave.finished, #625).
			result, err := s.config.Haul.propose(ctx, epoch)
			if err != nil {
				return Verdict{}, err
			}
			arbiter.propose("haul", result, func(outcome ProposalOutcome) {
				clockSchedulerLog("Haul.step result: reason=%v plan=%s", outcome.Verdict, outcome.Plan)
				out.Haul = &RoutineHaulResult{Verdict: outcome.Verdict, Plan: outcome.Plan, NativeWorkTicks: uint32(result.NativeWorkTicks)}
			})
			return Verdict{}, nil
		}},
	{name: "gear", goal: policy.MaintainEquipment, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.GearReplaceAction, domain.ApparelPolicyAction, domain.ProductionBillAction, domain.PolicyPruneAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Gear != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Gear.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Gear = &method
			return method.Verdict, nil
		}},
	{name: "foodStorageUpkeep", goal: policy.MaintainFoodStorage, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.HaulAction, domain.ProductionBillAction, domain.SupplyAllowAction, domain.SupplyForbidAction, domain.ZoneCreateAction}, sections: sectionsZones,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodStorageUpkeep != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.FoodStorageUpkeep.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.FoodStorageUpkeep = &method
			return method.Verdict, nil
		}},
	{name: "animalContainment", goal: policy.MaintainAnimalContainment, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
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
	{name: "populationCustody", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.CaptureAction, domain.UseItemAction, domain.RescueAction, domain.OpenCasketAction}, sections: sectionsCustody,
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationCustody != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PopulationCustody.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.PopulationCustody = &method
			return method.Verdict, nil
		}},
	{name: "populationJoiner", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.QuestAcceptAction, domain.RitualAction, domain.DialogAnswerAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationJoiner != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.PopulationJoiner.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.PopulationJoiner = &method
			return method.Verdict, nil
		}},
	{name: "research", goal: policy.EnsureResearch, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ResearchSelectAction, domain.BuildingAction}, sections: sectionsResearch,
		configured: func(c *ClockSchedulerConfig) bool { return c.Research != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Research.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Research.step result: reason=%v plan=%v nativeWorkTicks=%d", method.Verdict, method.Plan, method.NativeWorkTicks)
			out.Research = &method
			return method.Verdict, nil
		}},
	{name: "ingredient-storage", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ZoneCreateAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.IngredientStorage != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.IngredientStorage.step(ctx, epoch)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("IngredientStorage.step result: reason=%v plan=%v", method.Verdict, method.Plan)
			out.IngredientStorage = &method
			return method.Verdict, nil
		}},
	{name: "storage-shelves", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.StorageShelves != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.StorageShelves.step(ctx, epoch)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("StorageShelves.step result: reason=%v plan=%v", method.Verdict, method.Plan)
			out.StorageShelves = &method
			return method.Verdict, nil
		}},
	{name: "naming", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.NamingConfirmationAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Naming != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Naming.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Naming = &method
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
	{name: "trade", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.TradeAction}, sections: sectionsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Trade != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Trade.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("Trade.step result: reason=%v plan=%v trader=%s phase=%s", method.Verdict, method.Plan, method.Trader, method.Phase)
			out.Trade = &method
			return method.Verdict, nil
		}},
	{name: "resource", goal: policy.MaintainResource, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MineAcquisitionAction, domain.ProductionBillAction, domain.ZoneCreateAction, domain.BuildingAction, domain.DeconstructionAction}, sections: sectionsResource,
		configured: func(c *ClockSchedulerConfig) bool { return c.Resource != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Resource.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Resource = &method
			return method.Verdict, nil
		}},
	{name: "animalFeed", goal: policy.MaintainAnimalFeed, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MineAcquisitionAction, domain.ProductionBillAction, domain.ZoneCreateAction}, sections: sectionsZones,
		configured: func(c *ClockSchedulerConfig) bool { return c.AnimalFeed != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.AnimalFeed.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("AnimalFeed.step result: reason=%v plan=%s", method.Verdict, method.Plan)
			out.AnimalFeed = &method
			return method.Verdict, nil
		}},
	{name: "maintainShelter", goal: policy.MaintainShelter, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.AreaAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.MaintainShelter != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.MaintainShelter.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.MaintainShelter = &method
			return method.Verdict, nil
		}},
	{name: "firebreak", goal: policy.MaintainFirebreak, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AreaPlantCutAction, domain.CoverClearanceAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Firebreak != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Firebreak.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Firebreak = &method
			return method.Verdict, nil
		}},
	{name: "psylink", goal: policy.MaintainPsylink, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.UseItemAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Psylink != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Psylink.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Psylink = &method
			return method.Verdict, nil
		}},
	{name: "creepJoiners", goal: policy.ManageCreepJoiners, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.DropEquipmentAction, domain.SurgeryAction}, sections: sectionsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.CreepJoiners != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.CreepJoiners.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.CreepJoiners = &method
			return method.Verdict, nil
		}},
	{name: "ideoRoles", goal: policy.MaintainIdeoRoles, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AssignAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.IdeoRoles != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.IdeoRoles.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.IdeoRoles = &method
			return method.Verdict, nil
		}},
	{name: "rituals", goal: policy.MaintainRituals, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RitualAction}, sections: sectionsRituals,
		configured: func(c *ClockSchedulerConfig) bool { return c.Rituals != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Rituals.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Rituals = &method
			return method.Verdict, nil
		}},
	{name: "permits", goal: policy.MaintainPermits, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RoyaltyAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Permits != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Permits.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Permits = &method
			return method.Verdict, nil
		}},
	{name: "homeCoverage", goal: policy.MaintainHomeCoverage, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.AreaAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.HomeCoverage != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.HomeCoverage.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.HomeCoverage = &method
			return method.Verdict, nil
		}},
	{name: "stoneShell", goal: policy.MaintainStoneShell, class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.WallRemovalAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.StoneShell != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.StoneShell.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.StoneShell = &method
			return method.Verdict, nil
		}},
	{name: "tidy", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ZoneCreateAction, domain.ZoneDeleteAction, domain.DeconstructionAction, domain.MoveBuildingAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Tidy != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Tidy.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Tidy = &method
			return method.Verdict, nil
		}},
	{name: "stockpiles", goal: policy.MaintainStockpiles, class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ZoneCellEditAction, domain.StockpilePatchAction, domain.ZoneDeleteAction, domain.ZoneCreateAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Stockpiles != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.Stockpiles.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			out.Stockpiles = &method
			return method.Verdict, nil
		}},
	{name: "defenseLayout", goal: policy.EnsureDefensiveLayout, class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.RecoveryServiceAction, domain.CoverClearanceAction, domain.FoundationRemovalAction}, sections: sectionsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.DefenseLayout != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			method, err := s.config.DefenseLayout.step(ctx, epoch, arbiter)
			if err != nil {
				return Verdict{}, err
			}
			clockSchedulerLog("defense-layout.step: reason=%s tier=%s plan=%s nativeWorkTicks=%d", method.Verdict, method.Tier, method.Plan, method.NativeWorkTicks)
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
		if startup && entry.startup {
			entry.class = classCritical
		}
		queued = append(queued, entry.name)
		wave.queue(s, ctx, epoch, arbiter, entry)
	}
	return queued
}
