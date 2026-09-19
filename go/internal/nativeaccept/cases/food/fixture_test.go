package food

import (
	"math"
	"testing"
)

func TestEmptyChannelsRejectsCompetingOrUnknownFood(t *testing.T) {
	for _, name := range []string{"valid", "plants", "animals", "fields", "corpses", "producers", "unknown", "extra-stock", "held-stock", "other-food", "missing-nutrition", "nan", "inflated-nutrition"} {
		t.Run(name, func(t *testing.T) {
			row := map[string]any{"defName": "MealSurvivalPack", "units": float64(10), "spawned": true}
			audit := map[string]any{"plants": float64(0), "animals": float64(0), "fields": float64(0), "corpses": float64(0), "producers": float64(0), "stock": []any{row}}
			observed := map[string]any{"foodNutrition": float64(9)}
			prepared := map[string]any{"foodDef": "MealSurvivalPack", "declaredNutrition": float64(9)}
			switch name {
			case "valid":
			case "unknown":
				delete(audit, "plants")
			case "extra-stock":
				row["units"] = float64(11)
			case "held-stock":
				row["spawned"] = false
			case "other-food":
				row["defName"] = "RawBerries"
			case "missing-nutrition":
				delete(observed, "foodNutrition")
			case "nan":
				observed["foodNutrition"] = math.NaN()
			case "inflated-nutrition":
				observed["foodNutrition"] = float64(10)
			default:
				audit[name] = float64(1)
			}
			err := checkEmpty(audit, observed, prepared, 10)
			if (err == nil) != (name == "valid") {
				t.Fatalf("checkEmpty: %v", err)
			}
		})
	}
}
