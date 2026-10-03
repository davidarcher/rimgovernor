package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shells per built mortar: a base stock, doubled at the fabrication tier.
// EMP is stocked only when mech threats are plausible, read as a threat
// tier of fabrication (raid points at armoryFabricationPoints and up, the
// band where mechanoid raids and clusters become routine).
const (
	shellHEPerMortar         = 10
	shellIncendiaryPerMortar = 5
	shellEMPPerMortar        = 5
)

// MortarShellTargets is the shell stock the armory keeps for the built
// mortars under assessment a: none without a mortar or an observed threat,
// and none of a kind the load's shells (#1723) have no def for.
func MortarShellTargets(mortars int, a ArmoryAssessment, shells MortarShells) []Amount {
	if mortars <= 0 || a.Threat == ArmoryTierUnknown {
		return nil
	}
	scale := int64(mortars)
	if a.Tier >= ArmoryTierFabrication {
		scale *= 2
	}
	var targets []Amount
	add := func(kind ShellKind, perMortar int64) {
		if def := shells.Def(kind); def != "" {
			targets = append(targets, Amount{Resource: Resource(def), Count: perMortar * scale})
		}
	}
	add(ShellHE, shellHEPerMortar)
	add(ShellIncendiary, shellIncendiaryPerMortar)
	if a.Threat >= ArmoryTierFabrication {
		add(ShellEMP, shellEMPPerMortar)
	}
	return targets
}

// ShellsShort reports whether any target's stock is below half its count,
// the point a native stock-target bill resumes at: above it a standing bill
// has nothing to add. Unknown stock with targets is unknown; no targets is
// known false.
func ShellsShort(targets []Amount, stock domain.Fact[map[Resource]int64]) domain.Fact[bool] {
	if len(targets) == 0 {
		return domain.Known(false)
	}
	have, known := stock.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, t := range targets {
		if have[t.Resource]*2 < t.Count {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// ShellBill is one stock-target shell bill.
type ShellBill struct {
	Bench, Recipe string
	Target        int64
}

// SelectShellBill picks the first target, in priority order, that no bench
// bill already produces, on the first bench (by ID) with an available
// recipe making it. ok is false when every shell has a bill or none can be
// made; unknown bench rows count as unable. With holds (MaintainResource
// floors, #1230), a recipe whose known stock less the holds cannot fund one
// shell is skipped: its standing bill would spend the reserve.
func SelectShellBill(benches []GearBench, targets []Amount, stock []Stock, holds []Amount) (ShellBill, bool) {
	billed := map[Resource]bool{}
	for _, b := range benches {
		bills, _ := b.Bills.Value()
		for _, bill := range bills {
			for _, p := range bill.Products {
				billed[p] = true
			}
		}
	}
	sorted := slices.Clone(benches)
	slices.SortFunc(sorted, func(a, b GearBench) int {
		if a.ID < b.ID {
			return -1
		}
		if a.ID > b.ID {
			return 1
		}
		return 0
	})
	for _, t := range targets {
		if billed[t.Resource] || t.Count <= 0 {
			continue
		}
		for _, b := range sorted {
			recipes, known := b.Recipes.Value()
			if !known {
				continue
			}
			for _, r := range recipes {
				available, ak := r.Available.Value()
				on, ok := r.AvailableOn.Value()
				if ak && available && ok && on && slices.Contains(r.Products, t.Resource) && shellFunded(r, stock, holds) {
					return ShellBill{Bench: b.ID, Recipe: r.Definition, Target: t.Count}, true
				}
			}
		}
	}
	return ShellBill{}, false
}

// shellFunded reports whether a shell recipe may be billed under holds: no
// holds, unknown ingredients or an unmeasured stock all fund it (native
// waits for materials); only a known stock the holds leave short refuses.
func shellFunded(r GearRecipe, stock []Stock, holds []Amount) bool {
	slots, known := r.Ingredients.Value()
	if len(holds) == 0 || !known {
		return true
	}
	_, _, ok, unknown := gearIngredients(slots, "", GearPlanningRequest{Stock: stock, Holds: holds})
	return ok || unknown
}

// ResourceHolds is the MaintainResource floors as armory holds, sorted by
// resource.
func ResourceHolds(targets map[Resource]int64) []Amount {
	holds := make([]Amount, 0, len(targets))
	for resource, count := range targets {
		if count > 0 {
			holds = append(holds, Amount{Resource: resource, Count: count})
		}
	}
	slices.SortFunc(holds, func(a, b Amount) int { return strings.Compare(string(a.Resource), string(b.Resource)) })
	return holds
}
