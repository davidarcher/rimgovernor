package buildingruntime

import (
	"context"
	"log/slog"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// plannerGoals names the goal each single-goal planner serves: the record
// its refusal reason is filed on (GoalProgress.Planner). Planners serving
// several goals or none are left out.
var plannerGoals = map[string]policy.GoalID{
	"foodStorage": policy.EnsureFoodStorage, "foodAcquisition": policy.EnsureFoodSupply, "pestAcquisition": policy.ClearPests,
	"resourceAcquisition": policy.MaintainResource, "resource": policy.MaintainResource, "power": policy.EnsureBasicPower,
	"temperature": policy.EnsureTemperatureSafety, "refrigeration": policy.MaintainRefrigeration, "lighting": policy.MaintainLighting,
	"flooring": policy.MaintainFlooring, "routes": policy.MaintainRoutes, "cooking": policy.EnsureCooking,
	"basicComfort": policy.EnsureBasicComfort, "comfort": policy.EnsureComfort, "expansion": policy.EnsureExpansion,
	"equip": policy.MaintainEquipment, "gear": policy.MaintainEquipment, "secureSupplies": policy.SecureSupplies,
	"repair": policy.MaintainEssentialRepairs, "fireSafety": policy.MaintainFireSafety, "clearance": policy.ClearHomeObstructions,
	"shrine": policy.ClearAncientShrine, "clean": policy.MaintainCleanFacilities, "blight": policy.RemoveBlight,
	"waste": policy.MaintainWaste, "haul": policy.MaintainStorage, "foodStorageUpkeep": policy.MaintainFoodStorage,
	"animalContainment": policy.MaintainAnimalContainment, "research": policy.EnsureResearch, "animalFeed": policy.MaintainAnimalFeed,
	"homeCoverage": policy.MaintainHomeCoverage, "stoneShell": policy.MaintainStoneShell, "stockpiles": policy.MaintainStockpiles,
	"defenseLayout": policy.EnsureDefensiveLayout, "work": policy.EnsureWorkAssignments, "medical": policy.MaintainMedicalReserves,
}

// plannerRecordReason is what a planner's reason files on its goal's
// record: "" clears a refusal (the planner admitted, found work or saw no
// deficit), false skips it (no review to judge, or unprintable text).
func plannerRecordReason(reason RoutineBuildingReason) (string, bool) {
	switch reason {
	case "", BuildingMethodNoReview:
		return "", false
	case BuildingMethodAdmitted, BuildingMethodExistingWork, BuildingMethodNoDeficit, BuildingMethodHoldFallback, BuildingMethodCombatOrders:
		return "", true
	}
	s := string(reason)
	if len(s) > 96 {
		s = s[:96]
	}
	for i := 0; i < len(s); i++ {
		if s[i] < 0x21 || s[i] > 0x7e {
			return "", false
		}
	}
	return s, true
}

// plannerReasonLog remembers the last reason filed per goal so the service
// log names each goal's refusal once per change. Only the step goroutine
// touches it (recordWave).
type plannerReasonLog struct {
	last map[policy.GoalID]string
}

// changed files reasons and returns those that differ from the last seen.
func (l *plannerReasonLog) changed(reasons map[policy.GoalID]string) map[policy.GoalID]string {
	if l.last == nil {
		l.last = map[policy.GoalID]string{}
	}
	out := map[policy.GoalID]string{}
	for goal, reason := range reasons {
		if prior, seen := l.last[goal]; !seen || prior != reason {
			l.last[goal] = reason
			out[goal] = reason
		}
	}
	return out
}

// wavePlannerReasons collects the goal reasons of the planners that
// returned: a refusal wins over a clear from a sibling planner.
func wavePlannerReasons(names []string, reasonOf func(string) (RoutineBuildingReason, bool)) map[policy.GoalID]string {
	out := map[policy.GoalID]string{}
	for _, name := range names {
		goal, ok := plannerGoals[name]
		if !ok {
			continue
		}
		raw, ok := reasonOf(name)
		if !ok {
			continue
		}
		reason, ok := plannerRecordReason(raw)
		if !ok {
			continue
		}
		if prior, seen := out[goal]; !seen || prior == "" {
			out[goal] = reason
		}
	}
	return out
}

// recordPlannerReasons logs each goal's planner refusal on change and
// files it on the goal's progress record, so the status strip and the
// service log say why a goal has no method.
func (s *ClockScheduler) recordPlannerReasons(call context.Context, wave *plannerWave) {
	reasons := wavePlannerReasons(wave.finishedNames(), wave.reason)
	if len(reasons) == 0 {
		return
	}
	for goal, reason := range s.plannerReasons.changed(reasons) {
		if reason == "" {
			slog.Default().Info("planner refusal cleared", telemetry.ComponentKey, "clock-scheduler", "goal", string(goal))
		} else {
			slog.Default().Info("planner refused", telemetry.ComponentKey, "clock-scheduler", "goal", string(goal), "reason", reason)
		}
	}
	if s.player == nil || s.player.journal == nil {
		return
	}
	if _, err := s.player.journal.RecordPlannerReasons(call, reasons); err != nil {
		clockSchedulerLog("planner reasons: %v", err)
	}
}
