package buildingruntime

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// plannerEntry is one routine planner the clock step can queue. The table
// replaces the inline per-planner blocks Step used to carry: the same set,
// priorities and queue order, now selectable by step reason.
type plannerEntry struct {
	name string
	// class says whether the admission cycle waits on the planner (#623):
	// critical for the preempt and critical priority classes and the
	// emergency evidence (fire safety), optional for the development
	// reviews. TestPlannerCatalogClasses holds the rule.
	class    plannerClass
	priority int
	// kinds are the action kinds the planner dispatches. A terminal outcome
	// of one of these kinds is the planner's own work completing, so a wake
	// carrying it re-runs the planner (and only planners of that kind).
	kinds []domain.ActionKind
	// families are the fact families the planner plans from. An
	// ObservationInvalidated event naming one re-runs the planner.
	families []bridge.FactFamily
	// configured reports whether the composition wired this planner.
	configured func(*ClockSchedulerConfig) bool
	// run steps the planner and stores its result on out; the error is
	// wrapped with the planner's name.
	run func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error
}

var (
	factsBuilding = []bridge.FactFamily{bridge.FactColony, bridge.FactRooms}
	factsColony   = []bridge.FactFamily{bridge.FactColony}
	factsPawns    = []bridge.FactFamily{bridge.FactPawns}
	factsMedical  = []bridge.FactFamily{bridge.FactPawns, bridge.FactColony}
	factsThreat   = []bridge.FactFamily{bridge.FactPawns, bridge.FactEmergency}
)

// plannerCatalog lists every routine planner in queue order. Priorities
// follow policy.DetectRoutine's class for the planner's goal; queue order
// breaks priority ties (plannerGroup.Wait is stable), so the order here is
// the order Step queued them inline.
var plannerCatalog = []plannerEntry{
	{name: "idleDrafts", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.OwnedDraftAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Routine != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			return s.config.Routine.restoreIdleDrafts(ctx, epoch, arbiter)
		}},
	{name: "work", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.WorkAssignmentAction}, families: factsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.Work != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Work.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Work.step result: reason=%v plan=%v", method.Reason, method.Plan)
			out.Work = &method
			return nil
		}},
	{name: "fields", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.ZoneCreateAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Fields != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			started := time.Now()
			method, err := s.config.Fields.step(ctx, epoch, arbiter)
			if err != nil {
				clockSchedulerLog("Fields.step failed after %s: %v", time.Since(started), err)
				return err
			}
			clockSchedulerLog("Fields.step result: reason=%v plan=%s wait=%d", method.Reason, method.Plan, method.NativeWorkTicks)
			out.Fields = &method
			return nil
		}},
	{name: "foodStorage", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ZoneCreateAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodStorage != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.FoodStorage.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("FoodStorage.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.FoodStorage = &method
			return nil
		}},
	{name: "foodAcquisition", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.FoodAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("FoodAcquisition.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.FoodAcquisition = &method
			return nil
		}},
	{name: "pestAcquisition", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.PestAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PestAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("PestAcquisition.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.PestAcquisition = &method
			return nil
		}},
	{name: "woodAcquisition", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AcquisitionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.WoodAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.WoodAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.WoodAcquisition = &method
			return nil
		}},
	{name: "supplies", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.SupplyAllowAction, domain.SupplyForbidAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Supplies != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Supplies.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Supplies = &method
			return nil
		}},
	{name: "sleeping", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Sleeping != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Sleeping.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Sleeping.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.Sleeping = &method
			return nil
		}},
	{name: "power", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Power != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Power.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Power.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Power = &method
			return nil
		}},
	{name: "temperature", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Temperature != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Temperature.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Temperature.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Temperature = &method
			return nil
		}},
	{name: "refrigeration", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.BuildingTemperatureAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Refrigeration != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Refrigeration.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Refrigeration.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Refrigeration = &method
			return nil
		}},
	{name: "lighting", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Lighting != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Lighting.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Lighting.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Lighting = &method
			return nil
		}},
	{name: "flooring", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Flooring != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Flooring.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Flooring.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Flooring = &method
			return nil
		}},
	{name: "routes", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Routes != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Routes.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Routes.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Routes = &method
			return nil
		}},
	{name: "cooking", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Cooking != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Cooking.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Cooking.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.Cooking = &method
			return nil
		}},
	{name: "butcher", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Butcher != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Butcher.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Butcher.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.Butcher = &method
			return nil
		}},
	{name: "cookingBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.CookingBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.CookingBills.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.CookingBills = &method
			return nil
		}},
	{name: "preservationBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.PreservationBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PreservationBills.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PreservationBills = &method
			return nil
		}},
	{name: "butcherBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction, domain.ZoneCreateAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.ButcherBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.ButcherBills.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.ButcherBills = &method
			return nil
		}},
	{name: "cookAheadBills", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.CookAheadBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.CookAheadBills.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.CookAheadBills = &method
			return nil
		}},
	{name: "basicComfort", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.BasicComfort != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.BasicComfort.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("BasicComfort.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.BasicComfort = &method
			return nil
		}},
	{name: "comfort", class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Comfort != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Comfort.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Comfort = &method
			return nil
		}},
	{name: "workshop", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Workshop != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Workshop.stepWorkshops(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Workshop.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.Workshop = &method
			return nil
		}},
	{name: "hospital", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.BuildingAction, domain.BedMedicalAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Hospital != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Hospital.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Hospital.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.Hospital = &method
			return nil
		}},
	{name: "sleepingUpkeep", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.BuildingAction, domain.BedAssignAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.SleepingUpkeep != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.SleepingUpkeep.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("SleepingUpkeep.step result: reason=%v admitted=%v refused=%v ticks=%d", method.Reason, method.Decision.Admitted, method.Decision.Refused, method.NativeWorkTicks)
			out.SleepingUpkeep = &method
			return nil
		}},
	{name: "expansion", class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.ExcavationAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Expansion != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Expansion.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Expansion = &method
			return nil
		}},
	{name: "defense", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.OwnedDraftAction, domain.MeleeAttackAction, domain.RangedAttackAction, domain.MovementAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Defense != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Defense.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Defense.step result: reason=%v plan=%v", method.Reason, method.Plan)
			out.Defense = &method
			return nil
		}},
	{name: "medical", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.ProductionBillAction, domain.WorkAssignmentAction}, families: factsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Medical != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Medical.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Medical.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.Medical = &method
			return nil
		}},
	{name: "tend", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.TendAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Tend != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Tend.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Tend = &method
			return nil
		}},
	{name: "rescue", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.RescueAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Rescue != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Rescue.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Rescue = &method
			return nil
		}},
	{name: "equip", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.EquipAction}, families: factsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Equip != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Equip.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Equip = &method
			return nil
		}},
	{name: "secureSupplies", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.HaulAction, domain.ZoneCreateAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.SecureSupplies != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			// Migrated (#622): the planner proposes; the coordinator after
			// the wave ranks and commits.
			result, err := s.config.SecureSupplies.propose(ctx, epoch)
			if err != nil {
				return err
			}
			arbiter.propose("secureSupplies", result, func(outcome ProposalOutcome) {
				out.SecureSupplies = &RoutineSecureSuppliesResult{Reason: outcome.Reason, Plan: outcome.Plan}
			})
			return nil
		}},
	{name: "repair", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RepairAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Repair != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Repair.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Repair = &method
			return nil
		}},
	{name: "fireSafety", class: classCritical, priority: plannerFoothold, families: []bridge.FactFamily{bridge.FactEmergency, bridge.FactColony},
		configured: func(c *ClockSchedulerConfig) bool { return c.FireSafety != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, _ *stepArbiter) error {
			method, err := s.config.FireSafety.step(ctx, epoch)
			if err != nil {
				return err
			}
			clockSchedulerLog("FireSafety.step result: reason=%v outcome=%v nativeWorkTicks=%d", method.Reason, method.Outcome, method.NativeWorkTicks)
			out.FireSafety = &method
			return nil
		}},
	{name: "clearance", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.DeconstructionAction, domain.ZoneCreateAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clearance != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Clearance.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Clearance = &method
			return nil
		}},
	{name: "shrine", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.OwnedDraftAction, domain.MovementAction, domain.DeconstructionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Shrine != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Shrine.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Shrine = &method
			return nil
		}},
	{name: "clean", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CleanAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clean != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Clean.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Clean = &method
			return nil
		}},
	{name: "blight", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CutPlantAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Blight != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Blight.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Blight = &method
			return nil
		}},
	{name: "waste", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.WasteAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Waste != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Waste.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Waste = &method
			return nil
		}},
	{name: "moodRelief", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MoodReliefAction}, families: factsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.MoodRelief != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.MoodRelief.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.MoodRelief = &method
			return nil
		}},
	{name: "haul", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.HaulAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Haul != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			// Migrated (#622): the planner proposes; the coordinator after
			// the wave ranks and commits.
			result, err := s.config.Haul.propose(ctx, epoch)
			if err != nil {
				return err
			}
			arbiter.propose("haul", result, func(outcome ProposalOutcome) {
				clockSchedulerLog("Haul.step result: reason=%v plan=%s", outcome.Reason, outcome.Plan)
				out.Haul = &RoutineHaulResult{Reason: outcome.Reason, Plan: outcome.Plan}
			})
			return nil
		}},
	{name: "gear", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.GearReplaceAction, domain.ApparelPolicyAction, domain.ProductionBillAction}, families: factsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Gear != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Gear.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Gear = &method
			return nil
		}},
	{name: "foodStorageUpkeep", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.HaulAction, domain.ProductionBillAction, domain.SupplyAllowAction, domain.SupplyForbidAction, domain.ZoneCreateAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodStorageUpkeep != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.FoodStorageUpkeep.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.FoodStorageUpkeep = &method
			return nil
		}},
	{name: "animalContainment", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.AnimalContainment != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.AnimalContainment.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.AnimalContainment = &method
			return nil
		}},
	{name: "recovery", class: classCritical, priority: plannerCritical, kinds: []domain.ActionKind{domain.RecoveryServiceAction, domain.WorkAssignmentAction, domain.HusbandryAction}, families: []bridge.FactFamily{bridge.FactPawns, bridge.FactEmergency, bridge.FactColony},
		configured: func(c *ClockSchedulerConfig) bool { return c.Recovery != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Recovery.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Recovery = &method
			return nil
		}},
	{name: "husbandry", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.HusbandryAction}, families: factsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.Husbandry != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Husbandry.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Husbandry = &method
			return nil
		}},
	{name: "prisonerInteraction", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.PrisonerInteractionAction}, families: factsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.PrisonerInteraction != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PrisonerInteraction.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PrisonerInteraction = &method
			return nil
		}},
	{name: "populationCustody", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.CaptureAction, domain.RescueAction, domain.OpenCasketAction}, families: []bridge.FactFamily{bridge.FactPawns, bridge.FactEmergency, bridge.FactColony},
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationCustody != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PopulationCustody.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PopulationCustody = &method
			return nil
		}},
	{name: "populationJoiner", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.QuestAcceptAction, domain.DialogAnswerAction}, families: factsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationJoiner != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PopulationJoiner.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PopulationJoiner = &method
			return nil
		}},
	{name: "research", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ResearchSelectAction, domain.BuildingAction}, families: []bridge.FactFamily{bridge.FactResearch, bridge.FactColony, bridge.FactRooms},
		configured: func(c *ClockSchedulerConfig) bool { return c.Research != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Research.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Research.step result: reason=%v plan=%v nativeWorkTicks=%d", method.Reason, method.Plan, method.NativeWorkTicks)
			out.Research = &method
			return nil
		}},
	{name: "ingredient-storage", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ZoneCreateAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.IngredientStorage != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.IngredientStorage.step(ctx, epoch)
			if err != nil {
				return err
			}
			clockSchedulerLog("IngredientStorage.step result: reason=%v plan=%v", method.Reason, method.Plan)
			out.IngredientStorage = &method
			return nil
		}},
	{name: "naming", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.NamingConfirmationAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Naming != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Naming.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Naming = &method
			return nil
		}},
	{name: "dialog", class: classCritical, priority: plannerPreempt, kinds: []domain.ActionKind{domain.DialogAnswerAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Dialog != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Dialog.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Dialog = &method
			return nil
		}},
	{name: "trade", class: classOptional, priority: plannerFoothold, kinds: []domain.ActionKind{domain.TradeAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Trade != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Trade.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Trade.step result: reason=%v plan=%v trader=%s phase=%s", method.Reason, method.Plan, method.Trader, method.Phase)
			out.Trade = &method
			return nil
		}},
	{name: "resource", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MineAcquisitionAction, domain.ProductionBillAction, domain.ZoneCreateAction, domain.BuildingAction, domain.DeconstructionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Resource != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Resource.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Resource = &method
			return nil
		}},
	{name: "animalFeed", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MineAcquisitionAction, domain.ProductionBillAction, domain.ZoneCreateAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.AnimalFeed != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.AnimalFeed.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("AnimalFeed.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.AnimalFeed = &method
			return nil
		}},
	{name: "productionPolicy", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionPolicyAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.ProductionPolicy != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.ProductionPolicy.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.ProductionPolicy = &method
			return nil
		}},
	{name: "caravanJourney", class: classOptional, priority: plannerMaintenance, families: []bridge.FactFamily{bridge.FactWorld},
		configured: func(c *ClockSchedulerConfig) bool { return c.CaravanJourney != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.CaravanJourney.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.CaravanJourney = &method
			return nil
		}},
	{name: "homeCoverage", class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.HomeCoverageAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.HomeCoverage != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.HomeCoverage.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.HomeCoverage = &method
			return nil
		}},
	{name: "stoneShell", class: classOptional, priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.WallRemovalAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.StoneShell != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.StoneShell.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.StoneShell = &method
			return nil
		}},
	{name: "defenseLayout", class: classOptional, priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.RecoveryServiceAction, domain.CoverClearanceAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.DefenseLayout != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.DefenseLayout.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("defense-layout.step: reason=%s tier=%s plan=%s nativeWorkTicks=%d", method.Reason, method.Tier, method.Plan, method.NativeWorkTicks)
			out.DefenseLayout = &method
			return nil
		}},
}

// queuePlanners queues every configured catalog planner selected by pick
// onto the wave, returning the names queued in catalog order. A nil pick
// selects the whole catalog.
func (s *ClockScheduler) queuePlanners(ctx, epoch context.Context, wave *plannerWave, arbiter *stepArbiter, pick func(plannerEntry) bool) []string {
	var queued []string
	for _, entry := range s.catalog {
		if !entry.configured(&s.config) || pick != nil && !pick(entry) {
			continue
		}
		queued = append(queued, entry.name)
		wave.queue(s, ctx, epoch, arbiter, entry)
	}
	return queued
}
