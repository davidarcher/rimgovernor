package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// GearRecipeIngredients revisits 05.5's previously documented GearProduce
// blocker: "IngredientRequirement exposes only one Required/Available/
// Missing triple per slot, not per-alternative amounts, which does not
// cleanly map to policy.GearRecipe.Ingredients domain.Fact[[][]Amount]'s
// per-alternative cost model." That is only half true. gearIngredients
// (policy/gear.go) never reads a recipe's own reported Available/Missing —
// funding is decided separately against GearPlanningRequest.Stock — so only
// each alternative's required Resource/Count needs to come from the native
// recipe at all, and IngredientRequirement.Required does carry that, per
// slot.
//
// What genuinely cannot be recovered from one static IngredientRequirement
// row is a *different* required count per allowed alternative: RimWorld's
// small-volume scaling and nutrition-valued recipes (kibble, meals) make
// the unit count depend on which permitted item is chosen, and this bridge
// has no per-material recipe-cost preview to resolve that. A slot naming
// several allowed items (any meat, any hay) therefore maps every
// alternative at the slot's one reported count. That count is the slot's
// value, so funding against it is optimistic, never pessimistic: it only
// decides whether a StockTarget bill is worth queueing, and the native
// bill's own ingredient scan remains authoritative for what gets consumed.
//
// This narrows, but does not close, the GearProduce blocker: no bridge
// census yet requests populated RecipeState.Ingredients for gear benches at
// all (ReadColonyFacts's planning census explicitly nils Ingredients for
// its cooking/butchering RecipeState rows — see colony_production.go's
// validateColonyProduction), so wiring GearProduce still needs that read
// added first, on top of this translation.
func GearRecipeIngredients(rows []*o.IngredientRequirement) domain.Fact[[][]policy.Amount] {
	if len(rows) > 256 {
		return domain.Unknown[[][]policy.Amount]()
	}
	slots := make([][]policy.Amount, 0, len(rows))
	for _, row := range rows {
		slot, known := gearIngredientSlot(row)
		if !known {
			return domain.Unknown[[][]policy.Amount]()
		}
		slots = append(slots, slot)
	}
	return domain.Known(slots)
}

func gearIngredientSlot(row *o.IngredientRequirement) ([]policy.Amount, bool) {
	if row == nil || !row.GetComplete() || row.Required == nil {
		return nil, false
	}
	if len(row.Alternatives) > 0 {
		return gearIngredientAlternatives(row)
	}
	names := row.GetAllowedDefNames()
	if len(names) == 0 || len(names) > 256 {
		return nil, false
	}
	required := row.GetRequired()
	count := int64(required)
	if float64(count) != required || count <= 0 {
		return nil, false
	}
	seen := map[string]bool{}
	slot := make([]policy.Amount, 0, len(names))
	for _, name := range names {
		if validID(name) != nil || seen[name] {
			return nil, false
		}
		seen[name] = true
		slot = append(slot, policy.Amount{Resource: policy.Resource(name), Count: count})
	}
	return slot, true
}

// gearIngredientAlternatives maps a slot whose native read resolved the
// required count per allowed material (RecipeState.ingredients[].alternatives,
// the per-material CountRequiredOfFor the doc comment above describes as
// otherwise unrecoverable) into one alternative per material. Every listed
// alternative must be a whole positive count over an allowed definition.
func gearIngredientAlternatives(row *o.IngredientRequirement) ([]policy.Amount, bool) {
	if len(row.Alternatives) > 256 {
		return nil, false
	}
	allowed := map[string]bool{}
	for _, name := range row.GetAllowedDefNames() {
		allowed[name] = true
	}
	seen := map[string]bool{}
	out := make([]policy.Amount, 0, len(row.Alternatives))
	for _, q := range row.Alternatives {
		if q == nil || q.Units == nil || q.GetUnits() <= 0 || validID(q.GetDefName()) != nil || !allowed[q.GetDefName()] || seen[q.GetDefName()] {
			return nil, false
		}
		seen[q.GetDefName()] = true
		out = append(out, policy.Amount{Resource: policy.Resource(q.GetDefName()), Count: q.GetUnits()})
	}
	return out, true
}
