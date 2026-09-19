package trade

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const stockedSteel = 2000
const stockedFloor = 500

func init() {
	cases.Register(cases.Case{
		Name:   "trade/routine-stocked",
		Scope:  "An oversized steel hoard under the item wealth share policy sells exactly stock minus the retained floor; the native post-trade census equals that floor.",
		Start:  cases.Fixture{Op: "test/trade_fixture", Args: map[string]any{"action": "routine_setup", "silver": routineSilver, "steel": stockedSteel}},
		Serve:  &cases.ServeSpec{Families: []string{"trade"}, NativeTimeout: 15 * time.Second, Prefix: "trade-stocked", Extra: []string{"--routine-item-wealth-share", "0.01"}},
		Budget: 4 * time.Minute,
		Run:    runStockedTrade,
	})
}

func runStockedTrade(ctx context.Context, s cases.Session) error {
	if success, _ := na.AsBool(s.Prepared()["success"]); !success {
		return fmt.Errorf("stocked fixture failed: %v", s.Prepared())
	}
	readSteel := func(ctx context.Context, h *na.Harness, label string) (float64, error) {
		facts, err := h.Call(ctx, label, "home/colony_facts", nil)
		if err != nil {
			return 0, err
		}
		resources, ok := na.AsMap(facts["resources"])
		if !ok {
			return 0, fmt.Errorf("native resource census absent")
		}
		return na.AsNumber(resources["Steel"]), nil
	}
	accepted := false
	var sold int64
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 2 * time.Minute, Goal: policy.TradeWithCaravan, Until: func(sample map[string]any) bool {
			journal, err := na.OpenStoreWithRetry(ctx, s.Config().Output+"/service.sqlite")
			if err != nil {
				return false
			}
			defer journal.Close()
			sold = 0
			for _, key := range []string{"plans", "retired_plans"} {
				plans, _ := sample[key].([]map[string]any)
				for _, row := range plans {
					plan, err := journal.LoadPlan(ctx, domain.PlanID(na.AsString(row["plan"])))
					if err != nil {
						return false
					}
					for _, progress := range plan.Progress {
						trade, ok := progress.Action().Trade()
						if !ok || progress.View().Stage != domain.Completed {
							continue
						}
						if trade.Kind() == domain.TradeSetLines {
							for _, line := range trade.Lines() {
								if line.AbsoluteCount < 0 {
									sold -= int64(line.AbsoluteCount)
								}
							}
						}
						if trade.Kind() == domain.TradeAccept {
							for _, floor := range trade.EconomicFloors() {
								if floor.DefName == "Steel" && floor.Count == stockedFloor {
									accepted = true
								}
							}
						}
					}
				}
			}
			return accepted
		}},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			before, err := readSteel(ctx, h, "stocked-before")
			report["steel_before"] = before
			if err != nil {
				return err
			}
			if before != stockedSteel {
				return fmt.Errorf("steel stock = %v, want %d", before, stockedSteel)
			}
			return nil
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			after, err := readSteel(ctx, h, "stocked-after")
			if err != nil {
				return err
			}
			report["steel_after"], report["sold"], report["floor"] = after, sold, stockedFloor
			if !accepted || sold != stockedSteel-stockedFloor || after != stockedFloor {
				return fmt.Errorf("stocked trade: accepted=%v sold=%d want=%d census=%v floor=%d", accepted, sold, stockedSteel-stockedFloor, after, stockedFloor)
			}
			return nil
		},
	})
	return err
}
