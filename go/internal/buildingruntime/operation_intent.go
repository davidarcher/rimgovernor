package buildingruntime

import (
	"context"
	"log/slog"
	"strings"
	"unicode"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// goalLabels are the in-game names of goal kinds whose split identifier
// reads poorly; every other kind falls back to its words (#822).
var goalLabels = map[policy.GoalID]string{
	policy.EnsureFoodSupply:         "Food supply",
	policy.MaintainFoodStorage:      "Food reserve",
	policy.EnsureFoodStorage:        "Food storage",
	policy.MaintainResource:         "Resource",
	policy.MaintainFlooring:         "Flooring",
	policy.MaintainSleeping:         "Bedroom",
	policy.EnsureInitialShelter:     "Shelter",
	policy.EnsureTemperatureSafety:  "Temperature",
	policy.EnsureCooking:            "Cooking",
	policy.EnsureBasicPower:         "Power",
	policy.EnsureBasicDefense:       "Defense",
	policy.EnsureDefensiveLayout:    "Defense layout",
	policy.CriticalMedicine:         "Medical",
	policy.MaintainMedicalCare:      "Medical care",
	policy.MaintainMedicalReserves:  "Medicine",
	policy.MaintainRefrigeration:    "Refrigeration",
	policy.EnsureComfort:            "Comfort",
	policy.EnsureBasicComfort:       "Comfort",
	policy.EnsureResearch:           "Research",
	policy.EnsureWorkAssignments:    "Work",
	policy.ManageSupplySafety:       "Supply safety",
	policy.AllowStartingSupplies:    "Starting supplies",
	policy.MaintainEquipment:        "Equipment",
	policy.MaintainEssentialRepairs: "Repairs",
	policy.MaintainCleanFacilities:  "Cleaning",
	policy.TradeWithCaravan:         "Trade",
	policy.ActiveCombat:             "Combat",
	policy.RemoveBlight:             "Blight",
	policy.MaintainHerd:             "Herd",
	policy.MaintainAnimalFeed:       "Animal feed",
	policy.MaintainPopulation:       "Population",
}

// GoalLabel is the readable name of a stored goal id. Routine and player ids
// end in "-<kind>"; the kind picks the label.
func GoalLabel(goal domain.GoalID) string {
	kind := string(goal)
	if i := strings.LastIndexByte(kind, '-'); i >= 0 {
		kind = kind[i+1:]
	}
	if label, ok := goalLabels[policy.GoalID(kind)]; ok {
		return label
	}
	return splitWords(strings.TrimPrefix(strings.TrimPrefix(strings.TrimPrefix(kind, "Maintain"), "Ensure"), "Manage"))
}

// methodReason is the method id without its identity hash: "hunt-<hex>" is
// "hunt", "comfort-Armchair" stays whole.
func methodReason(method domain.MethodID) string {
	parts := strings.Split(string(method), "-")
	if n := len(parts); n > 1 && len(parts[n-1]) >= 16 && strings.Trim(parts[n-1], "0123456789abcdef") == "" {
		parts = parts[:n-1]
	}
	return strings.Join(parts, " ")
}

// OperationIntent is Operation.intent for a goal method's writes.
func OperationIntent(method domain.GoalMethod) string {
	return bridge.FormatOperationIntent(GoalLabel(method.Goal), methodReason(method.Method))
}

func splitWords(s string) string {
	var b strings.Builder
	for i, r := range s {
		if i > 0 && unicode.IsUpper(r) {
			b.WriteByte(' ')
			r = unicode.ToLower(r)
		}
		b.WriteRune(r)
	}
	return b.String()
}

// withPlanIntent carries the plan's goal method intent to the writes the
// dispatch issues; a plan no goal admitted, or a failed lookup, dispatches
// without one.
func withPlanIntent(ctx context.Context, intents interface {
	PlanGoalMethod(context.Context, domain.PlanID) (domain.GoalMethod, bool, error)
}, plan domain.PlanID) context.Context {
	method, ok, err := intents.PlanGoalMethod(ctx, plan)
	if err != nil {
		slog.Default().Debug("operation intent lookup failed", telemetry.ComponentKey, "dispatch", "plan", string(plan), "error", err)
		return ctx
	}
	if !ok {
		return ctx
	}
	return bridge.WithOperationIntent(ctx, OperationIntent(method))
}
