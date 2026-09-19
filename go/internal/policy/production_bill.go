package policy

import (
	"math"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type BillPurpose string

const (
	CookFood     BillPurpose = "cook"
	PreserveFood BillPurpose = "preserve"
	ButcherFood  BillPurpose = "butcher"
	// CookAheadFood is MaintainRefrigeration's answer to a solar flare
	// (#408): the warm perishable stock the dark coolers cannot save is
	// cooked into meals on whichever bench still works (a fuelled stove;
	// native's usable flag already excludes the unpowered electric one),
	// so the colony eats it before it rots instead of waiting out the
	// outage. The target is the at-risk nutrition beyond what existing
	// bills already reserve, in meals; the caller gates it on the flare.
	CookAheadFood BillPurpose = "cook_ahead"
)

type ProductionProduct struct {
	Name                       string
	Nutrition, Demand, RotDays domain.Fact[float64]
	Edible, Perishable         domain.Fact[bool]
}
type ProductionRecipe struct {
	Name                                       string
	Available                                  domain.Fact[bool]
	Products                                   []ProductionProduct
	Mood, NutrientEfficiency, WorkPerNutrition domain.Fact[float64]
	IngredientClasses                          domain.Fact[[]FoodIngredientSlot]
	NeedsPower                                 domain.Fact[bool]
	CookSkillFloor                             domain.Fact[int32]
}

// ExistingProductionBill's TargetCount/Forever describe the bill's own
// configured target, not how much of it is already produced: a TargetCount
// bill reserves that nutrition toward its buffer even while still filling it.
type ExistingProductionBill struct {
	Humanlike   bool
	ID          string
	Managed     domain.Fact[bool]
	Active      domain.Fact[bool]
	Recipe      string
	TargetCount domain.Fact[int32]
	Forever     domain.Fact[bool]
}
type ProductionBench struct {
	HumanButchers        []HumanButcherCandidate
	HumanCorpseNutrition domain.Fact[float64]
	HumanStorageCells    []domain.Cell
	HumanStorageReady    domain.Fact[bool]
	HumanCorpseDef       string
	ID, Definition       string
	Token                domain.Fact[string]
	Usable               domain.Fact[bool]
	Butcher              bool
	// Room is the native room census identity the bench stands in.
	Room    domain.Fact[string]
	Recipes []ProductionRecipe
	Bills   []ExistingProductionBill
}
type BillSelection struct {
	Ingredients          []string
	Worker               string
	Bench, Recipe, Token string
	Replace              string
	Mode                 domain.BillMode
	Target               int32
}

// ProductionBillContext carries purpose-specific reviewed inputs. Meal context
// is supplied by the food-plan owner; its absence preserves ordinary cooking.
type ProductionBillContext struct {
	Reserve *FoodReserveReview
	Meals   *MealTierRequest
}

// billAdequate is whether an existing bill of the selected recipe already
// does the selected work: active, and repeating forever or to at least the
// selected target. A bill whose state cannot be read counts as adequate, so
// an unreadable bill is left alone rather than replaced blind.
func billAdequate(bill ExistingProductionBill, selection BillSelection) bool {
	active, ak := bill.Active.Value()
	forever, fk := bill.Forever.Value()
	target, tk := bill.TargetCount.Value()
	if !ak || !fk {
		return true
	}
	if !active {
		return false
	}
	if selection.Mode == domain.ButcherForever || forever {
		return true
	}
	return !tk || target >= selection.Target
}

// OrdinaryMealRecipe is a bill recipe native lets a meal bill replace: the
// game's CookMeal* recipes short of the survival pack, the tier family the
// meal review chooses among. Reserve (pemmican) and butcher bills are never
// replaced through this path.
func OrdinaryMealRecipe(name string) bool {
	return strings.HasPrefix(name, "CookMeal") && name != "CookMealSurvival"
}

// An existing bill of the selected recipe stands when it already does the
// work; a suspended or undersized one is corrected by replacement (#461:
// whoever configured it, Auto plans it fresh), which native admits for
// ordinary meal recipes only, so a butcher bill in that state is left as it
// is. A cook-ahead bill is the one addition: it adds the meals the at-risk
// stock needs beyond every existing bill's reserved target, so a bench
// already cooking to a smaller target gets a second, larger bill for the
// outage.
func SelectProductionBill(purpose BillPurpose, benches domain.Fact[[]ProductionBench], colonists domain.Fact[int64], runway, atRisk domain.Fact[float64], targetDays float64, context ...ProductionBillContext) (BillSelection, bool) {
	rows, known := benches.Value()
	count, ck := colonists.Value()
	if !known || !ck || count <= 0 || count > 256 || len(rows) > 256 || !fieldPositive(targetDays) || targetDays > 60 {
		return BillSelection{}, false
	}
	if purpose != CookFood && purpose != PreserveFood && purpose != ButcherFood && purpose != CookAheadFood {
		return BillSelection{}, false
	}
	if len(context) > 1 {
		return BillSelection{}, false
	}
	if len(context) == 1 && (context[0].Meals != nil && purpose != CookFood || context[0].Reserve != nil && purpose != PreserveFood) {
		return BillSelection{}, false
	}
	if purpose == CookFood && len(context) == 1 && context[0].Meals != nil {
		if context[0].Meals.TargetDays != targetDays {
			return BillSelection{}, false
		}
		return selectMealBill(benches, colonists, *context[0].Meals)
	}
	if purpose == PreserveFood {
		if len(context) != 1 || context[0].Reserve == nil {
			return BillSelection{}, false
		}
		return SelectReserveBill(benches, *context[0].Reserve)
	}
	reserved, ok := ReservedFoodNutrition(rows)
	if !ok {
		reserved = 0
	}
	ahead := 0.0
	if purpose == CookAheadFood {
		risk, rk := atRisk.Value()
		if !rk || !fieldPositive(risk) || risk > 1e6 {
			return BillSelection{}, false
		}
		ahead = risk - reserved
		if ahead <= 0 {
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
			selection := BillSelection{Bench: bench.ID, Recipe: recipe.Name, Token: token, Mode: domain.FoodTarget, Target: int32(count * 3)}
			if purpose == ButcherFood {
				if recipe.Name != "ButcherCorpseFlesh" {
					continue
				}
				selection.Mode = domain.ButcherForever
				selection.Target = 0
			}
			if purpose != CookAheadFood {
				exists := false
				for _, bill := range bench.Bills {
					if bill.Recipe != recipe.Name || bill.Humanlike {
						continue
					}
					exists = true
					if !billAdequate(bill, selection) && bill.ID != "" && OrdinaryMealRecipe(recipe.Name) && (selection.Replace == "" || bill.ID < selection.Replace) {
						selection.Replace = bill.ID
					}
				}
				if exists && selection.Replace == "" {
					continue
				}
			}
			if purpose != ButcherFood {
				if len(recipe.Products) != 1 {
					continue
				}
				product := recipe.Products[0]
				edible, ek := product.Edible.Value()
				nutrition, nk := product.Nutrition.Value()
				if !ek || !edible || !nk || !fieldPositive(nutrition) {
					continue
				}
				if purpose == CookAheadFood {
					target := math.Ceil(ahead / nutrition)
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
	// Butchery belongs away from the cooking workspace (issue #6): a butcher
	// bench standing in no cooking bench's room wins over one that shares.
	separated := map[string]bool{}
	if purpose == ButcherFood {
		separated = SeparatedButcherBenches(rows)
	}
	sort.Slice(options, func(i, j int) bool {
		a, b := options[i], options[j]
		if (purpose == CookFood || purpose == CookAheadFood) && (a.Recipe == "CookMealSimple") != (b.Recipe == "CookMealSimple") {
			return a.Recipe == "CookMealSimple"
		}
		if separated[a.Bench] != separated[b.Bench] {
			return separated[a.Bench]
		}
		if a.Recipe != b.Recipe {
			return a.Recipe < b.Recipe
		}
		return a.Bench < b.Bench
	})
	return options[0], true
}

// SeparatedButcherBenches reports, by ID, every butcher bench whose room is
// known and holds no cooking bench. A bench with an unknown room is never
// certified separated.
func SeparatedButcherBenches(benches []ProductionBench) map[string]bool {
	cooking := map[string]bool{}
	for _, bench := range benches {
		if room, known := bench.Room.Value(); known && !bench.Butcher {
			cooking[room] = true
		}
	}
	result := map[string]bool{}
	for _, bench := range benches {
		if room, known := bench.Room.Value(); known && bench.Butcher && !cooking[room] {
			result[bench.ID] = true
		}
	}
	return result
}

// AllButchersColocated is true when at least one butcher bench exists, every
// butcher bench's room is known, and none stands apart from cooking: the
// separated-spot build then owns the food-supply goal before any bill.
func AllButchersColocated(benches []ProductionBench) bool {
	separated := SeparatedButcherBenches(benches)
	butchers := 0
	for _, bench := range benches {
		if !bench.Butcher {
			continue
		}
		if _, known := bench.Room.Value(); !known || separated[bench.ID] {
			return false
		}
		butchers++
	}
	return butchers > 0
}
