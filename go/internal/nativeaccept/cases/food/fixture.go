// Package food verifies isolated nutrition channels against native facts.
package food

import (
	"context"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
)

const prepareOp = "test/food_channels_prepare"
const observeOp = "test/food_channels_observe"

// EmptyChannels starts on the Core tribal8 save with no food channels and
// exactly units of the named stock. The runner quiets the storyteller and
// freezes needs. Channel cases add their source after this start and before
// starting a service; Keep controls which needs their simulation exercises.
func EmptyChannels(foodDef string, units int) cases.Start {
	return cases.Fixture{On: cases.Save{Name: sustained.BaselineSave}, Op: prepareOp,
		Args: map[string]any{"foodDef": foodDef, "stockUnits": units}}
}

func init() {
	cases.Register(cases.Case{
		Name: "food/empty-channels", Scope: "Empty-channel fixture has no competing sources; native foodNutrition equals only the declared stock, including after a zero-stock reset.",
		Start: EmptyChannels("MealSurvivalPack", 10), RequiredOps: []string{observeOp, prepareOp}, Budget: 2 * time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			if err := checkFixture(ctx, s, s.Prepared(), "stocked", 10); err != nil {
				return err
			}
			prepared, err := s.Harness().Call(ctx, "reset", prepareOp, map[string]any{"stockUnits": 0})
			if err != nil {
				return err
			}
			return checkFixture(ctx, s, prepared, "empty", 0)
		},
	})
}

func checkFixture(ctx context.Context, s cases.Session, prepared map[string]any, label string, units int) error {
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("fixture refused: %v", prepared)
	}
	ms, ok := prepared["preparationMs"].(float64)
	if !ok || ms < 0 || ms >= 60000 {
		return fmt.Errorf("fixture preparation must take under a minute: %v", prepared["preparationMs"])
	}
	audit, err := s.Harness().Call(ctx, label+"-channels", observeOp, nil)
	if err != nil {
		return err
	}
	reply, err := s.Harness().Wire(ctx, label+"-facts", "observations_read_colony_facts", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	s.Report()[label] = map[string]any{"prepared": prepared, "channels": audit, "foodNutrition": observed["foodNutrition"]}
	return checkEmpty(audit, observed, prepared, units)
}

func checkEmpty(audit, observed, prepared map[string]any, units int) error {
	for _, key := range []string{"fields", "plants", "animals", "corpses", "producers"} {
		value, ok := audit[key].(float64)
		if !ok || value != 0 {
			return fmt.Errorf("fixture has unknown or nonzero %s: %v", key, audit[key])
		}
	}
	stock, ok := audit["stock"].([]any)
	if !ok {
		return fmt.Errorf("fixture stock unobserved")
	}
	count := 0.0
	for _, raw := range stock {
		row, _ := na.AsMap(raw)
		n, valid := row["units"].(float64)
		if !valid || n <= 0 || math.IsNaN(n) || math.IsInf(n, 0) || row["defName"] != prepared["foodDef"] || row["spawned"] != true {
			return fmt.Errorf("unexpected food stock: %v", raw)
		}
		count += n
	}
	if count != float64(units) {
		return fmt.Errorf("stock units %v, want %d", count, units)
	}
	declared, declaredOK := prepared["declaredNutrition"].(float64)
	actual, actualOK := observed["foodNutrition"].(float64)
	if !declaredOK || !actualOK || math.IsNaN(declared) || math.IsNaN(actual) || math.IsInf(declared, 0) || math.IsInf(actual, 0) || declared < 0 || (units > 0 && declared <= 0) || (units == 0 && declared != 0) || math.Abs(actual-declared) > 1e-5 {
		return fmt.Errorf("native foodNutrition %v does not equal declared stock %v", observed["foodNutrition"], prepared["declaredNutrition"])
	}
	return nil
}
