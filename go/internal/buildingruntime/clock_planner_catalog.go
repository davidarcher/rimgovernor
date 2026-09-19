package buildingruntime

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// plannerEntry is one routine planner the clock step can queue. The table
// replaces the inline per-planner blocks Step used to carry: the same set,
// priorities and queue order, now selectable by step reason.
type plannerEntry struct {
	name     string
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
	{name: "work", priority: plannerFoothold, kinds: []domain.ActionKind{domain.WorkAssignmentAction}, families: factsPawns,
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
	{name: "fields", priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.ZoneCreateAction}, families: factsBuilding,
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
	{name: "foodStorage", priority: plannerFoothold, kinds: []domain.ActionKind{domain.ZoneCreateAction}, families: factsBuilding,
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
	{name: "foodAcquisition", priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.FoodAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.FoodAcquisition = &method
			return nil
		}},
	{name: "pestAcquisition", priority: plannerFoothold, kinds: []domain.ActionKind{domain.AcquisitionAction}, families: factsColony,
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
	{name: "woodAcquisition", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.AcquisitionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.WoodAcquisition != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.WoodAcquisition.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.WoodAcquisition = &method
			return nil
		}},
	{name: "supplies", priority: plannerCritical, kinds: []domain.ActionKind{domain.SupplyAllowAction, domain.SupplyForbidAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Supplies != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Supplies.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Supplies = &method
			return nil
		}},
	{name: "sleeping", priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "power", priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "temperature", priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "refrigeration", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.BuildingTemperatureAction}, families: factsBuilding,
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
	{name: "lighting", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "flooring", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "routes", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "cooking", priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "butcher", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Butcher != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Butcher.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Butcher = &method
			return nil
		}},
	{name: "cookingBills", priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.CookingBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.CookingBills.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.CookingBills = &method
			return nil
		}},
	{name: "preservationBills", priority: plannerFoothold, kinds: []domain.ActionKind{domain.ProductionBillAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.PreservationBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PreservationBills.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PreservationBills = &method
			return nil
		}},
	{name: "butcherBills", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionBillAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.ButcherBills != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.ButcherBills.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.ButcherBills = &method
			return nil
		}},
	{name: "basicComfort", priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "comfort", priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Comfort != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Comfort.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Comfort = &method
			return nil
		}},
	{name: "workshop", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
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
	{name: "hospital", priority: plannerCritical, kinds: []domain.ActionKind{domain.BuildingAction, domain.BedMedicalAction}, families: factsBuilding,
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
	{name: "sleepingUpkeep", priority: plannerCritical, kinds: []domain.ActionKind{domain.BuildingAction, domain.BedAssignAction}, families: factsBuilding,
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
	{name: "expansion", priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.ExcavationAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Expansion != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Expansion.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Expansion = &method
			return nil
		}},
	{name: "defense", priority: plannerPreempt, kinds: []domain.ActionKind{domain.OwnedDraftAction, domain.MeleeAttackAction, domain.RangedAttackAction, domain.MovementAction}, families: factsThreat,
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
	{name: "tend", priority: plannerCritical, kinds: []domain.ActionKind{domain.TendAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Tend != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Tend.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Tend = &method
			return nil
		}},
	{name: "rescue", priority: plannerCritical, kinds: []domain.ActionKind{domain.RescueAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Rescue != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Rescue.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Rescue = &method
			return nil
		}},
	{name: "equip", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.EquipAction}, families: factsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Equip != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Equip.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Equip = &method
			return nil
		}},
	{name: "secureSupplies", priority: plannerFoothold, kinds: []domain.ActionKind{domain.BuildingAction, domain.HaulAction, domain.ZoneCreateAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.SecureSupplies != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.SecureSupplies.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.SecureSupplies = &method
			return nil
		}},
	{name: "repair", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.RepairAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Repair != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Repair.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Repair = &method
			return nil
		}},
	{name: "fireSafety", priority: plannerFoothold, families: []bridge.FactFamily{bridge.FactEmergency, bridge.FactColony},
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
	{name: "clearance", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.DeconstructionAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clearance != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Clearance.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Clearance = &method
			return nil
		}},
	{name: "clean", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CleanAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.Clean != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Clean.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Clean = &method
			return nil
		}},
	{name: "blight", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.CutPlantAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Blight != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Blight.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Blight = &method
			return nil
		}},
	{name: "waste", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.WasteAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Waste != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Waste.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Waste = &method
			return nil
		}},
	{name: "moodRelief", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MoodReliefAction}, families: factsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.MoodRelief != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.MoodRelief.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.MoodRelief = &method
			return nil
		}},
	{name: "haul", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.HaulAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Haul != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Haul.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			clockSchedulerLog("Haul.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.Haul = &method
			return nil
		}},
	{name: "gear", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.GearReplaceAction, domain.ProductionBillAction}, families: factsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.Gear != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Gear.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Gear = &method
			return nil
		}},
	{name: "medical", priority: plannerCritical, kinds: []domain.ActionKind{domain.AcquisitionAction, domain.ProductionBillAction}, families: factsMedical,
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
	{name: "foodStorageUpkeep", priority: plannerFoothold, kinds: []domain.ActionKind{domain.HaulAction, domain.ProductionBillAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.FoodStorageUpkeep != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.FoodStorageUpkeep.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.FoodStorageUpkeep = &method
			return nil
		}},
	{name: "animalContainment", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.AnimalContainment != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.AnimalContainment.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.AnimalContainment = &method
			return nil
		}},
	{name: "recovery", priority: plannerCritical, kinds: []domain.ActionKind{domain.RecoveryServiceAction, domain.WorkAssignmentAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.Recovery != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Recovery.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Recovery = &method
			return nil
		}},
	{name: "husbandry", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.HusbandryAction}, families: factsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.Husbandry != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Husbandry.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Husbandry = &method
			return nil
		}},
	{name: "prisonerInteraction", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.PrisonerInteractionAction}, families: factsPawns,
		configured: func(c *ClockSchedulerConfig) bool { return c.PrisonerInteraction != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PrisonerInteraction.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PrisonerInteraction = &method
			return nil
		}},
	{name: "populationCustody", priority: plannerFoothold, kinds: []domain.ActionKind{domain.CaptureAction, domain.RescueAction}, families: factsThreat,
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationCustody != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PopulationCustody.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PopulationCustody = &method
			return nil
		}},
	{name: "populationJoiner", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.QuestAcceptAction, domain.DialogAnswerAction}, families: factsMedical,
		configured: func(c *ClockSchedulerConfig) bool { return c.PopulationJoiner != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.PopulationJoiner.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.PopulationJoiner = &method
			return nil
		}},
	{name: "research", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ResearchSelectAction, domain.BuildingAction}, families: []bridge.FactFamily{bridge.FactResearch, bridge.FactColony, bridge.FactRooms},
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
	{name: "ingredient-storage", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ZoneCreateAction}, families: factsBuilding,
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
	{name: "naming", priority: plannerPreempt, kinds: []domain.ActionKind{domain.NamingConfirmationAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Naming != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Naming.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Naming = &method
			return nil
		}},
	{name: "dialog", priority: plannerPreempt, kinds: []domain.ActionKind{domain.DialogAnswerAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Dialog != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Dialog.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Dialog = &method
			return nil
		}},
	{name: "trade", priority: plannerFoothold, kinds: []domain.ActionKind{domain.TradeAction}, families: factsColony,
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
	{name: "resource", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MineAcquisitionAction, domain.ProductionBillAction, domain.ZoneCreateAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.Resource != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.Resource.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.Resource = &method
			return nil
		}},
	{name: "animalFeed", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.MineAcquisitionAction, domain.ProductionBillAction, domain.ZoneCreateAction}, families: factsColony,
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
	{name: "productionPolicy", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.ProductionPolicyAction}, families: factsColony,
		configured: func(c *ClockSchedulerConfig) bool { return c.ProductionPolicy != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.ProductionPolicy.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.ProductionPolicy = &method
			return nil
		}},
	{name: "caravanJourney", priority: plannerMaintenance, families: []bridge.FactFamily{bridge.FactWorld},
		configured: func(c *ClockSchedulerConfig) bool { return c.CaravanJourney != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.CaravanJourney.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.CaravanJourney = &method
			return nil
		}},
	{name: "homeCoverage", priority: plannerComfort, kinds: []domain.ActionKind{domain.HomeCoverageAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.HomeCoverage != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.HomeCoverage.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.HomeCoverage = &method
			return nil
		}},
	{name: "stoneShell", priority: plannerComfort, kinds: []domain.ActionKind{domain.BuildingAction, domain.WallRemovalAction}, families: factsBuilding,
		configured: func(c *ClockSchedulerConfig) bool { return c.StoneShell != nil },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) error {
			method, err := s.config.StoneShell.step(ctx, epoch, arbiter)
			if err != nil {
				return err
			}
			out.StoneShell = &method
			return nil
		}},
	{name: "defenseLayout", priority: plannerMaintenance, kinds: []domain.ActionKind{domain.BuildingAction, domain.RecoveryServiceAction}, families: factsBuilding,
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
// onto g, returning the names queued in catalog order. A nil pick selects
// the whole catalog.
func (s *ClockScheduler) queuePlanners(ctx, epoch context.Context, out *ClockSchedulerResult, g *plannerGroup, arbiter *stepArbiter, pick func(plannerEntry) bool) []string {
	var queued []string
	for _, entry := range plannerCatalog {
		if !entry.configured(&s.config) || pick != nil && !pick(entry) {
			continue
		}
		entry := entry
		queued = append(queued, entry.name)
		g.Go(entry.priority, func() error {
			if err := entry.run(s, ctx, epoch, out, arbiter); err != nil {
				return fmt.Errorf("%s: %w", entry.name, err)
			}
			return nil
		})
	}
	return queued
}
