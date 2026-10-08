// The child/first-child case (#1691, epic #1667) proves a Biotech colony
// raises its first child end to end in vanilla: a baby with no breastfeeder in
// the colony is fed through the baby food the MaintainBabyFeeding goal (#1681)
// has the colony cook, and grows into the Child developmental stage alive and
// not starved.
package child

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"slices"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	prepareTool = "test/baby_care_prepare"
	inspectTool = "test/baby_care_inspect"
	// billCeiling bounds the journal wait for the baby food bill; the stall
	// budget ends it earlier when nothing moves.
	billCeiling = 6 * time.Minute
	// growTicks bounds the native run after the service stops: the fixture
	// leaves the baby two game days short of the Child stage (120000 ticks).
	growTicks = 150000
)

func init() {
	cases.Register(cases.Case{
		Name: "child/first-child",
		Scope: "Issue #1691: with no breastfeeder in the colony, the MaintainBabyFeeding standard has the colony cook baby-edible food " +
			"and the baby is fed by it until it reaches the Child developmental stage. A Go snapshot over recorded facts cannot " +
			"cover it: the chain is vanilla's own (bottle-feeding on Childcare work, the baby's food need, ageing and the " +
			"stage change), asserted on the live pawn.",
		Start:       cases.Fixture{Op: prepareTool, On: cases.LabStart()},
		Expansions:  []string{"ludeon.rimworld.biotech"},
		NoKeep:      true,
		Quiet:       na.QuietRequired,
		RequiredOps: []string{prepareTool, inspectTool},
		// The shelter family keeps supervised windows running (the bill
		// planner alone never advances the clock); dialog answers the letters.
		Serve: &cases.ServeSpec{
			Families: []routinefamily.Family{routinefamily.Bill, routinefamily.Shelter, routinefamily.Dialog}, NativeTimeout: 15 * time.Second, Prefix: "child-first-child",
		},
		Budget: 15 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: runFirstChild,
	})
}

// babyFoodBill reports whether a bill is on the bench for one of the stove's
// baby-edible recipes. The planner picks among them (the bulk recipe over its
// single-item sibling), so the case accepts any.
func babyFoodBill(bill domain.ProductionBill, bench string, recipes []string) bool {
	return bill.Bench() == bench && slices.Contains(recipes, bill.Recipe())
}

// babyFoodBillPlaced reports whether the journal holds a completed bill for
// a baby-edible recipe on the fixture's stove.
func babyFoodBillPlaced(ctx context.Context, st *store.Store, bench string, recipes []string) (bool, error) {
	plans, err := st.PlanHistoryWithMethods(ctx, 256, "bill-*")
	if err != nil {
		return false, err
	}
	for _, p := range plans {
		for _, progress := range p.Progress {
			bill, ok := progress.Action().ProductionBill()
			if ok && babyFoodBill(bill, bench, recipes) && progress.View().Stage == domain.Completed {
				return true, nil
			}
		}
	}
	return false, nil
}

func runFirstChild(ctx context.Context, s cases.Session) error {
	report, prepared := s.Report(), s.Prepared()
	var h *na.Harness
	baby, bench := na.AsString(prepared["babyId"]), na.AsString(prepared["benchId"])
	var recipes []string
	for _, raw := range na.AsSlice(prepared["recipes"]) {
		recipes = append(recipes, na.AsString(raw))
	}
	if baby == "" || bench == "" || len(recipes) == 0 || na.AsNumber(prepared["foodLevel"]) <= 0 {
		return fmt.Errorf("fixture: unexpected first-child staging: %#v", prepared)
	}
	report["baby"], report["bench"], report["recipes"], report["fixture"] = baby, bench, recipes, prepared

	service, err := s.Serve(ctx, s.Spec())
	if err != nil {
		return err
	}
	defer service.Stop()
	if _, err := service.Acquire(); err != nil {
		return err
	}
	service.KeepAuthority(ctx)
	st, err := service.Store(ctx)
	if err != nil {
		return err
	}
	defer st.Close()

	// The colony's own answer to a baby it cannot breastfeed: the standing
	// baby food bill on its stove.
	err = na.WaitProgress(ctx, na.Wait{Ceiling: billCeiling, Stall: na.StallBudget(), Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		placed, err := babyFoodBillPlaced(ctx, st, bench, recipes)
		return na.Signature(placed), placed, err
	})
	if err != nil {
		return fmt.Errorf("baby food bill: %w", err)
	}
	report["run_keepalive"] = service.Stop()

	// Native postconditions after the service releases the game slot: the
	// bill stands on the stove for a baby-edible recipe, and the baby is fed
	// (its food level is above the staged level, which only ingestion
	// raises; the service run may already have carried it past the Child
	// stage) and reaches the Child stage alive and not starved.
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen session after service stop: %w", err)
	}
	args := map[string]any{"babyId": baby, "benchId": bench}
	var last map[string]any
	fed, stagedFood := false, na.AsNumber(prepared["foodLevel"])
	elapsed, err := na.RunUntil(ctx, h, "child-grown", growTicks, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		reply, err := h.Call(ctx, "baby-inspect", inspectTool, args)
		if err != nil {
			return "", false, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return "", false, fmt.Errorf("baby inspect refused: %v", reply)
		}
		last = reply
		if dead, _ := na.AsBool(reply["dead"]); dead {
			return "", false, fmt.Errorf("the baby died: %v", reply)
		}
		food := na.AsNumber(reply["foodLevel"])
		fed = fed || food > stagedFood
		return na.Signature(reply["stage"], food, fed), na.AsString(reply["stage"]) == "Child", nil
	})
	report["grow_ticks"], report["inspect"], report["fed"] = elapsed, last, fed
	if err != nil {
		return err
	}
	billed := false
	for _, raw := range na.AsSlice(last["bills"]) {
		row, _ := na.AsMap(raw)
		if edible, _ := na.AsBool(row["babyEdible"]); edible && slices.Contains(recipes, na.AsString(row["recipe"])) {
			billed = true
		}
	}
	if !billed {
		return fmt.Errorf("no baby food bill (%v) stands on bench %s: %v", recipes, bench, last["bills"])
	}
	if !fed {
		return fmt.Errorf("the baby's food level never rose above its staged %v, so it was never fed: %v", stagedFood, last)
	}
	if severity := na.AsNumber(last["malnutrition"]); severity > 0 {
		return fmt.Errorf("the baby reached the Child stage malnourished (severity %v): %v", severity, last)
	}
	return nil
}
