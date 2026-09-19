package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func rawFoodClass(v *o.FoodIngredientClass) domain.Fact[policy.FoodIngredientClass] {
	if v == nil {
		return domain.Unknown[policy.FoodIngredientClass]()
	}
	switch *v {
	case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT:
		return domain.Known(policy.IngredientMeat)
	case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_VEGETABLE:
		return domain.Known(policy.IngredientVegetable)
	case o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANIMAL_PRODUCT:
		return domain.Known(policy.IngredientAnimalProduct)
	default:
		return domain.Known(policy.FoodIngredientClass(""))
	}
}

// MealRequest uses observed work priorities, never a proposed roster. The raw
// forecast keeps competing consumers, private ownership and spoilage deadlines.
func (p ColonyProjection) MealRequest(minDays, targetDays float64) policy.MealTierRequest {
	r := policy.MealTierRequest{Plan: p.Facts.FoodPlan, MinDays: minDays, TargetDays: targetDays, Environment: p.Environment}
	if supply, known := p.CombinedFoodSupply.Value(); known {
		raw := supply
		raw.Stocks = nil
		complete := true
		for _, stock := range supply.Stocks {
			kind, known := stock.RawClass.Value()
			if !known {
				complete = false
				break
			}
			if kind != "" && !stock.Reserve && !stock.Corpse {
				raw.Stocks = append(raw.Stocks, stock)
			}
		}
		if complete {
			if f, err := policy.ForecastFood(raw, nil); err == nil {
				r.RawRunwayDays = f.RunwayDays
			}
		}
	}
	if pawns, known := p.WorkPawns.Value(); known {
		cooks := []policy.MealCook{}
		complete := true
		for _, pawn := range pawns {
			available, ak := pawn.Available.Value()
			work, wk := pawn.Work.Value()
			skills, sk := pawn.Skills.Value()
			if !ak || !wk || !sk {
				complete = false
				break
			}
			if !available {
				continue
			}
			for _, w := range work {
				if w.Work == policy.WorkCooking && w.Priority > 0 && !w.Disabled {
					for _, s := range skills {
						if s.Name == "Cooking" && !s.Disabled {
							cooks = append(cooks, policy.MealCook{Pawn: pawn.ID, Skill: int32(s.Level)})
						}
					}
				}
			}
		}
		if complete {
			r.Cooks = domain.Known(cooks)
			r.CookLaborConstrained = domain.Known(len(cooks) == 0)
		}
	}
	if pawns, known := p.Facts.MoodPawns.Value(); known {
		pressure, complete := false, true
		for _, pawn := range pawns {
			high, hk := pawn.HighExpectations.Value()
			mood, mk := pawn.Mood.Value()
			target, tk := pawn.Target.Value()
			if hk && high && mk && tk && mood < target {
				pressure = true
			}
			thoughts, known := pawn.Thoughts.Value()
			if !known {
				complete = false
				continue
			}
			for _, thought := range thoughts {
				if thought.Def == "HighExpectations" || thought.Def == "SkyHighExpectations" {
					pressure = true
				}
			}
		}
		if complete || pressure {
			r.HighExpectations = domain.Known(pressure)
		}
	}
	if benches, known := p.ProductionBenches.Value(); known {
		r.Previous = policy.ObservedMealTier(benches)
	}
	if channels, known := p.FoodChannels.Value(); known {
		for _, dispenser := range channels.PasteDispenser {
			powered, pk := dispenser.Powered.Value()
			nutrition, nk := dispenser.HopperNutrition.Value()
			if pk && powered && nk && nutrition > 0 {
				r.Previous = policy.MealPaste
				break
			}
		}
	}
	for _, d := range p.Definitions {
		if d.Name == "NutrientPasteDispenser" {
			r.Paste = domain.Known(policy.Infrastructure{Name: d.Name, Available: d.Available, PowerW: d.PowerW, Costs: d.Costs})
		}
	}
	return r
}
