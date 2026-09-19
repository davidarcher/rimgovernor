package trade

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/food"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const cropFloor = 5000

func init() {
	for _, mode := range []string{"bridge", "surplus"} {
		spec := cases.ServeSpec{Families: []string{"trade"}, NativeTimeout: 15 * time.Second, Prefix: "trade-food-" + mode}
		if mode == "surplus" {
			spec.Families = append(spec.Families, "resource")
			spec.Extra = []string{"--routine-resource-target", fmt.Sprintf("RawRice:%d", cropFloor)}
		}
		cases.Register(cases.Case{
			Name:  "trade/routine-food-" + mode,
			Scope: "Food trade through the live routine: one day of food buys pemmican; an active fine-meal bill buys meat with crop surplus while retaining the explicit crop floor. Native stock changes establish completion.",
			Start: cases.Fixture{On: food.EmptyChannels("MealSimple", 0), Op: "test/trade_fixture", Args: map[string]any{"action": "routine_setup", "foodMode": mode, "silver": routineSilver}},
			Serve: &spec, Budget: 4 * time.Minute,
			Run: func(ctx context.Context, s cases.Session) error { return runFoodTrade(ctx, s, mode) },
		})
	}
}

func runFoodTrade(ctx context.Context, s cases.Session, mode string) error {
	if success, _ := na.AsBool(s.Prepared()["success"]); !success {
		return fmt.Errorf("food trade fixture failed: %v", s.Prepared())
	}
	var before map[string]any
	read := func(ctx context.Context, h *na.Harness, label string) (map[string]any, error) {
		r, err := h.Call(ctx, label, "home/colony_facts", nil)
		if err != nil {
			return nil, err
		}
		stock, ok := na.AsMap(r["resources"])
		if !ok {
			return nil, fmt.Errorf("native resource census absent")
		}
		return stock, nil
	}
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 2 * time.Minute, Goal: policy.TradeWithCaravan, Until: func(sample map[string]any) bool {
			journal, err := na.OpenStoreWithRetry(ctx, s.Config().Output+"/service.sqlite")
			if err != nil {
				return false
			}
			defer journal.Close()
			for _, key := range []string{"plans", "retired_plans"} {
				plans, _ := sample[key].([]map[string]any)
				for _, row := range plans {
					plan, err := journal.LoadPlan(ctx, domain.PlanID(na.AsString(row["plan"])))
					if err != nil {
						continue
					}
					for _, p := range plan.Progress {
						if trade, ok := p.Action().Trade(); ok && trade.Kind() == domain.TradeAccept && p.View().Stage == domain.Completed {
							return true
						}
					}
				}
			}
			return false
		}},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			var err error
			before, err = read(ctx, h, "food-trade-before")
			report["food_trade_before"] = before
			if err != nil {
				return err
			}
			if mode == "bridge" {
				units := na.AsNumber(s.Prepared()["foodUnits"])
				demand := na.AsNumber(s.Prepared()["dailyNutrition"])
				if demand <= 0 || units*0.9 < demand || units*0.9 >= demand+0.91 {
					return fmt.Errorf("fixture is not one day of food: units=%v demand=%v", units, demand)
				}
			} else if na.AsNumber(before["RawRice"]) <= cropFloor {
				return fmt.Errorf("fixture has no crop surplus: %v", before)
			}
			return nil
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			after, err := read(ctx, h, "food-trade-after")
			if err != nil {
				return err
			}
			report["food_trade_after"] = after
			return checkFoodTrade(mode, before, after)
		},
	})
	return err
}

func checkFoodTrade(mode string, before, after map[string]any) error {
	count := func(rows map[string]any, name string) float64 {
		if v, present := rows[name]; present {
			return na.AsNumber(v)
		}
		return 0
	}
	if mode == "bridge" {
		if count(after, "Pemmican") <= count(before, "Pemmican") || count(after, "Silver") >= count(before, "Silver") {
			return fmt.Errorf("pemmican purchase did not land: before=%v after=%v", before, after)
		}
		return nil
	}
	if count(after, "Meat_Muffalo") <= count(before, "Meat_Muffalo") || count(after, "RawRice") >= count(before, "RawRice") || count(after, "RawRice") < cropFloor {
		return fmt.Errorf("crop-for-meat exchange or floor failed: before=%v after=%v floor=%d", before, after, cropFloor)
	}
	return nil
}
