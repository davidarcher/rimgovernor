package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"sort"
)

type BillPurpose string

const (
	CookFood     BillPurpose = "cook"
	PreserveFood BillPurpose = "preserve"
	ButcherFood  BillPurpose = "butcher"
)

type ProductionProduct struct {
	Name                       string
	Nutrition, Demand, RotDays domain.Fact[float64]
	Edible, Perishable         domain.Fact[bool]
}
type ProductionRecipe struct {
	Name      string
	Available domain.Fact[bool]
	Products  []ProductionProduct
}
type ExistingProductionBill struct{ Recipe string }
type ProductionBench struct {
	ID, Definition string
	Token          domain.Fact[string]
	Usable         domain.Fact[bool]
	Butcher        bool
	Recipes        []ProductionRecipe
	Bills          []ExistingProductionBill
}
type BillSelection struct {
	Bench, Recipe, Token string
	Mode                 domain.BillMode
	Target               int32
}

// Existing recipe bills belong to their player settings; no duplicate is a substitute
// for changing a suspended, filtered or smaller bill.
func SelectProductionBill(purpose BillPurpose, benches domain.Fact[[]ProductionBench], colonists domain.Fact[int64], runway, atRisk domain.Fact[float64], targetDays float64) (BillSelection, bool) {
	rows, known := benches.Value()
	count, ck := colonists.Value()
	if !known || !ck || count <= 0 || count > 256 || len(rows) > 256 || !fieldPositive(targetDays) || targetDays > 60 {
		return BillSelection{}, false
	}
	if purpose != CookFood && purpose != PreserveFood && purpose != ButcherFood {
		return BillSelection{}, false
	}
	if purpose == PreserveFood {
		days, dk := runway.Value()
		risk, rk := atRisk.Value()
		if !dk || !rk || !foodNumber(days) || !fieldPositive(risk) || days >= targetDays {
			return BillSelection{}, false
		}
	}
	var options []BillSelection
	seen := map[string]bool{}
	for _, bench := range rows {
		if !foodID(bench.ID) || seen[bench.ID] || len(bench.Recipes) > 256 || len(bench.Bills) > 15 {
			return BillSelection{}, false
		}
		seen[bench.ID] = true
		usable, uk := bench.Usable.Value()
		token, tk := bench.Token.Value()
		if !uk || !usable || !tk || !foodID(token) || len(bench.Bills) >= 15 || bench.Butcher != (purpose == ButcherFood) {
			continue
		}
		recipes := map[string]bool{}
		for _, recipe := range bench.Recipes {
			if !foodID(recipe.Name) || recipes[recipe.Name] {
				return BillSelection{}, false
			}
			recipes[recipe.Name] = true
			available, ak := recipe.Available.Value()
			if !ak || !available {
				continue
			}
			exists := false
			for _, bill := range bench.Bills {
				exists = exists || bill.Recipe == recipe.Name
			}
			if exists {
				continue
			}
			selection := BillSelection{Bench: bench.ID, Recipe: recipe.Name, Token: token, Mode: domain.FoodTarget, Target: int32(count * 3)}
			if purpose == ButcherFood {
				if recipe.Name != "ButcherCorpseFlesh" {
					continue
				}
				selection.Mode = domain.ButcherForever
				selection.Target = 0
			} else {
				if len(recipe.Products) != 1 {
					continue
				}
				product := recipe.Products[0]
				edible, ek := product.Edible.Value()
				nutrition, nk := product.Nutrition.Value()
				if !ek || !edible || !nk || !fieldPositive(nutrition) {
					continue
				}
				if purpose == PreserveFood {
					perish, pk := product.Perishable.Value()
					shelf, sk := product.RotDays.Value()
					demand, dk := product.Demand.Value()
					if !pk || perish && (!sk || !fieldPositive(shelf) || shelf <= targetDays) || !dk || !fieldPositive(demand) {
						continue
					}
					target := math.Ceil(demand * targetDays / nutrition)
					if target < 1 || target > 10000 {
						continue
					}
					selection.Target = int32(target)
				}
			}
			options = append(options, selection)
		}
	}
	if len(options) == 0 {
		return BillSelection{}, false
	}
	sort.Slice(options, func(i, j int) bool {
		a, b := options[i], options[j]
		if purpose == CookFood && (a.Recipe == "CookMealSimple") != (b.Recipe == "CookMealSimple") {
			return a.Recipe == "CookMealSimple"
		}
		if a.Recipe != b.Recipe {
			return a.Recipe < b.Recipe
		}
		return a.Bench < b.Bench
	})
	return options[0], true
}
