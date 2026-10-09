package buildingruntime

import (
	"context"
	"strings"
	"unicode"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// goalLabels are the in-game names of goal kinds whose split identifier
// reads poorly; every other kind falls back to its words.
var concernLabels = map[policy.ConcernID]string{
	policy.EnsureFoodSupply:         "Food supply",
	policy.MaintainFoodStorage:      "Food storage",
	policy.MaintainResource:         "Resource",
	policy.MaintainFlooring:         "Flooring",
	policy.MaintainHousing:          "Housing",
	policy.EnsureTemperatureSafety:  "Temperature",
	policy.EnsureCooking:            "Cooking",
	policy.MaintainButcherSpot:      "Butcher spot",
	policy.EnsureBasicPower:         "Power",
	policy.EnsureBasicDefense:       "Defense",
	policy.EnsureDefensiveLayout:    "Defense layout",
	policy.CriticalMedicine:         "Medical",
	policy.MaintainMedicalReserves:  "Medicine",
	policy.MaintainSurgery:          "Surgery",
	policy.MaintainRefrigeration:    "Refrigeration",
	policy.EnsureComfort:            "Comfort",
	policy.EnsureResearch:           "Research",
	policy.EnsureWorkAssignments:    "Work",
	policy.ManageSupplySafety:       "Supply safety",
	policy.MaintainEquipment:        "Equipment",
	policy.MaintainEssentialRepairs: "Repairs",
	policy.MaintainCleanFacilities:  "Cleaning",
	policy.TradeWithCaravan:         "Trade",
	policy.ActiveCombat:             "Combat",
	policy.RemoveBlight:             "Blight",
	policy.ManagePollution:          "Pollution",
	policy.EnsureMechCharger:        "Mech charger",
	policy.MaintainGeneBank:         "Gene bank",
	policy.MaintainWorkLedger:       "Production orders",
	policy.MaintainIncineration:     "Incineration",
	policy.MaintainHerd:             "Herd",
	policy.MaintainPopulation:       "Population",
}

// GoalLabel is the readable name of a stored goal id. Routine and player ids
// end in "-<kind>"; the kind picks the label.
func ConcernLabel(goal domain.ConcernID) string {
	kind := string(goal)
	if i := strings.LastIndexByte(kind, '-'); i >= 0 {
		kind = kind[i+1:]
	}
	if label, ok := concernLabels[policy.ConcernID(kind)]; ok {
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

// OperationIntent is Operation.intent for a method's writes: the
// planner's admission reason follows the method, "Food supply: acquire,
// food runway 1.5d".
func OperationIntent(method store.PlanMethod) string {
	why := methodReason(method.Method)
	if reason := strings.TrimSpace(method.Reason); reason != "" {
		why += ", " + reason
	}
	return bridge.FormatOperationIntent(ConcernLabel(method.Concern), why)
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

// withPlanIntent carries the plan's method intent to the writes the
// dispatch issues; a plan no goal admitted, or a failed lookup, dispatches
// without one.
func withPlanIntent(ctx context.Context, intents interface {
	PlanMethod(context.Context, domain.PlanID) (store.PlanMethod, bool, error)
}, plan domain.PlanID) context.Context {
	method, ok, err := intents.PlanMethod(ctx, plan)
	if err != nil {
		return ctx
	}
	if !ok {
		return ctx
	}
	return bridge.WithOperationIntent(ctx, OperationIntent(method))
}
