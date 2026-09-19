package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestProductionBillTakeoverDrift(t *testing.T) {
	for _, purpose := range []BillPurpose{CookFood, ButcherFood, PreserveFood} {
		for _, drift := range []string{"suspended", "target", "repeat", "filter", "worker", "skill", "unknown-other-fields", "adequate"} {
			t.Run(string(purpose)+"/"+drift, func(t *testing.T) {
				recipe := "CookMealSimple"
				product := "MealSimple"
				forever := purpose == ButcherFood
				if forever {
					recipe = "ButcherCorpseFlesh"
				}
				if purpose == PreserveFood {
					recipe, product = "MakePemmican", "Pemmican"
				}
				mode := "TargetCount"
				if forever {
					mode = "Forever"
				}
				bill := ExistingProductionBill{ID: "foreign", Recipe: recipe, Managed: domain.Known(false), Active: domain.Known(true), Forever: domain.Known(forever), RepeatMode: domain.Known(mode), TargetCount: domain.Known(int32(6)), DefaultIngredients: domain.Known(true), UnrestrictedWorker: domain.Known(true), Worker: domain.Known("")}
				switch drift {
				case "suspended":
					bill.Active = domain.Known(false)
				case "target":
					bill.TargetCount = domain.Known(int32(1))
					if forever {
						bill.Forever = domain.Known(false)
						bill.RepeatMode = domain.Known("TargetCount")
					}
				case "repeat":
					bill.RepeatMode = domain.Known("RepeatCount")
					bill.Forever = domain.Known(false)
				case "filter":
					bill.DefaultIngredients = domain.Known(false)
				case "worker":
					bill.Worker = domain.Known("absent-pawn")
				case "skill":
					bill.UnrestrictedWorker = domain.Known(false)
				case "unknown-other-fields":
					bill.Active = domain.Known(false)
					bill.Forever = domain.Unknown[bool]()
					bill.TargetCount = domain.Unknown[int32]()
				}
				bench := ProductionBench{ID: "bench", Token: domain.Known("token"), Usable: domain.Known(true), Butcher: forever, Recipes: []ProductionRecipe{{Name: recipe, Available: domain.Known(true), Products: []ProductionProduct{{Name: product, Edible: domain.Known(true), Nutrition: domain.Known(1.0)}}}}, Bills: []ExistingProductionBill{bill}}
				// A full stack must still permit a replacement, and unrelated recipes survive.
				for len(bench.Bills) < 15 {
					bench.Bills = append(bench.Bills, ExistingProductionBill{ID: "unrelated", Recipe: "OtherRecipe"})
				}
				var ctx []ProductionBillContext
				if purpose == PreserveFood {
					ctx = []ProductionBillContext{{Reserve: &FoodReserveReview{TargetNutrition: 6, DeficitNutrition: 6}}}
				}
				got, ok := SelectProductionBill(purpose, domain.Known([]ProductionBench{bench}), domain.Known(int64(2)), domain.Unknown[float64](), domain.Unknown[float64](), 7, ctx...)
				if drift == "adequate" {
					if ok {
						t.Fatalf("adequate bill replaced: %+v", got)
					}
					return
				}
				if !ok || got.Replace != "foreign" || got.Recipe != recipe {
					t.Fatalf("drift not corrected: %+v, %v", got, ok)
				}
			})
		}
	}
}

func TestMealTakeoverCorrectsDriftBesideAdequateBill(t *testing.T) {
	r := tierRequest()
	r.RawRunwayDays = domain.Known(1.0)
	r.Paste = domain.Unknown[Infrastructure]()
	benches, _ := tierBenches().Value()
	benches[0].Bills = []ExistingProductionBill{
		{ID: "adequate", Recipe: "CookMealSimple", Active: domain.Known(true), Forever: domain.Known(true)},
		{ID: "drifted", Recipe: "CookMealSimple", DefaultIngredients: domain.Known(false)},
	}
	got, ok := SelectProductionBill(CookFood, domain.Known(benches), domain.Known(int64(2)), r.RawRunwayDays, domain.Unknown[float64](), r.TargetDays, ProductionBillContext{Meals: &r})
	if !ok || got.Replace != "drifted" {
		t.Fatal(got, ok)
	}
}

func TestBillAdequacyUsesPlannedIngredients(t *testing.T) {
	wanted := BillSelection{Ingredients: []string{"Rice", "MeatHuman"}}
	bill := ExistingProductionBill{DefaultIngredients: domain.Known(false), Ingredients: domain.Known([]string{"MeatHuman", "Rice"})}
	if !billAdequate(bill, wanted) {
		t.Fatal("matching explicit diet replaced")
	}
	bill.Ingredients = domain.Known([]string{"Rice"})
	if billAdequate(bill, wanted) {
		t.Fatal("narrowed diet preserved")
	}
}
