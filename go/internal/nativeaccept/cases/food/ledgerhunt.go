package food

import (
	"context"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	huntPrepareOp = "test/ledger_hunt_prepare"
	huntButcherOp = "test/ledger_hunt_butcher"
	huntObserveOp = "test/ledger_hunt_observe"
)

func init() {
	cases.Register(cases.Case{Name: "food/ledger-hunt",
		Scope:       "Native read contract and end-to-end signal (a Go snapshot test cannot cover native hooks): a ranger kills one wild deer and a bill butchers it; the colony facts delivery ledger counts kill 1 and a butcher record whose meat equals the produced stack's independent tally with leather above zero, linked by corpse id, and a corpse re-placed twice (a haul) is not recounted.",
		Start:       cases.Fixture{Op: huntPrepareOp, On: cases.LabStart()},
		QuietWorld:  true,
		RequiredOps: []string{huntButcherOp, huntObserveOp}, Budget: 5 * time.Minute,
		Reason: "a lab with a ranger, one deer and a butcher table; a few native hours of hunting and one butchering",
		Run:    runLedgerHunt})
}

func runLedgerHunt(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	tally := func(label string) (map[string]any, error) { return h.Call(ctx, label, huntObserveOp, nil) }
	if _, err := na.RunUntil(ctx, h, "ledger-hunt-kill", 4000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		v, err := tally("ledger-hunt-progress")
		if err != nil {
			return "", false, err
		}
		s.Report()["hunt_tally"] = v
		return na.Signature(v["tick"]), na.AsNumber(v["kills"]) > 0, nil
	}); err != nil {
		return err
	}
	killed, err := readLedger(ctx, s, "ledger-hunt-killed")
	if err != nil {
		return err
	}
	if n := na.AsNumber(killed.raw["killsTotal"]); n != 1 || len(na.AsSlice(killed.raw["kills"])) != 1 {
		return fmt.Errorf("want one counted kill, got total %v: %v", n, killed.raw)
	}
	if len(na.AsSlice(killed.raw["butchers"])) != 0 {
		return fmt.Errorf("a butcher was counted before any bill: %v", killed.raw)
	}
	kill, _ := na.AsMap(na.AsSlice(killed.raw["kills"])[0])
	corpse := na.AsString(kill["corpseId"])
	if corpse == "" || na.AsString(kill["race"]) != "Deer" || na.AsNumber(kill["potentialNutrition"]) <= 0 || na.AsNumber(kill["bodySize"]) <= 0 {
		return fmt.Errorf("kill record is incomplete: %v", kill)
	}
	hauled, err := h.Call(ctx, "ledger-hunt-butcher", huntButcherOp, nil)
	if err != nil {
		return err
	}
	if na.AsString(hauled["corpse"]) != corpse {
		return fmt.Errorf("fixture corpse %v is not the counted kill %v", hauled["corpse"], corpse)
	}
	if _, err := na.RunUntil(ctx, h, "ledger-hunt-butcher", 4000, na.Wait{Stall: na.StallBudget()}, func(ctx context.Context) (string, bool, error) {
		v, err := tally("ledger-hunt-butcher-progress")
		if err != nil {
			return "", false, err
		}
		s.Report()["hunt_tally"] = v
		return na.Signature(v["tick"]), na.AsNumber(v["butcherMeat"]) > 0, nil
	}); err != nil {
		return err
	}
	done, err := tally("ledger-hunt-done")
	if err != nil {
		return err
	}
	ledger, err := readLedger(ctx, s, "ledger-hunt-facts")
	if err != nil {
		return err
	}
	s.Report()["ledger_hunt"] = map[string]any{"tally": done, "ledger": ledger.raw}
	if n := na.AsNumber(ledger.raw["killsTotal"]); n != 1 || na.AsNumber(done["kills"]) != 1 {
		return fmt.Errorf("a hauled corpse was recounted: ledger kills %v, fixture corpses %v", n, done["kills"])
	}
	if n := na.AsNumber(ledger.raw["butchersTotal"]); n != 1 || len(na.AsSlice(ledger.raw["butchers"])) != 1 {
		return fmt.Errorf("want one counted butcher, got total %v: %v", n, ledger.raw)
	}
	butcher, _ := na.AsMap(na.AsSlice(ledger.raw["butchers"])[0])
	if na.AsString(butcher["corpseId"]) != corpse {
		return fmt.Errorf("butcher record %v is not linked to corpse %v", butcher["corpseId"], corpse)
	}
	if meat := na.AsNumber(butcher["meatUnits"]); meat != na.AsNumber(done["butcherMeat"]) || meat <= 0 {
		return fmt.Errorf("butcher meat %v differs from the produced stack %v", meat, done["butcherMeat"])
	}
	if leather := na.AsNumber(butcher["leatherUnits"]); leather != na.AsNumber(done["butcherLeather"]) || leather <= 0 {
		return fmt.Errorf("butcher leather %v differs from the produced leather %v", leather, done["butcherLeather"])
	}
	if nutrition := na.AsNumber(butcher["meatNutrition"]); nutrition <= 0 || nutrition > na.AsNumber(kill["potentialNutrition"])*1.01 || math.IsNaN(nutrition) {
		return fmt.Errorf("butchered nutrition %v is not within the kill's potential %v", nutrition, kill["potentialNutrition"])
	}
	return nil
}
