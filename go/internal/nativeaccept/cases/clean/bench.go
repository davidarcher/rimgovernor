package clean

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "clean/bench",
		Scope: "Opportunistic cleaning before a bill (#2515): with eight old filth beside a fuelled bench, the patched native " +
			"WorkGiver_DoBill hands a normal pawn a Clean job capped at five filth, while a pawn with Cleaning at priority 0 " +
			"and a drafted pawn still get the bill. A Go snapshot cannot prove the Harmony patch on vanilla job selection.",
		Start: cases.LabStart(), RequiredOps: []string{"test/bench_cleaning"},
		Budget: 3 * time.Minute, Crew: cases.Crew{Size: 3},
		Run: runBench,
	})
}

func runBench(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "bench-cleaning-authority", s.Identity()); err != nil {
		return err
	}
	row, err := h.Call(ctx, "bench-cleaning", "test/bench_cleaning", map[string]any{})
	if err != nil {
		return err
	}
	s.Report()["probe"] = row
	if ok, _ := na.AsBool(row["success"]); !ok {
		return fmt.Errorf("bench_cleaning refused: %#v", row)
	}
	if active, _ := na.AsBool(row["supervisorActive"]); !active {
		return fmt.Errorf("supervisor not active: %#v", row)
	}
	if row["cleanBefore"] != "DoBill" {
		return fmt.Errorf("a clean bench must start the bill: %#v", row)
	}
	if row["normal"] != "Clean" || na.AsNumber(row["normalTargets"]) != 5 {
		return fmt.Errorf("a normal pawn must clean exactly the five-filth cap first: %#v", row)
	}
	for _, who := range []string{"disabled", "drafted"} {
		if row[who] != "DoBill" || na.AsNumber(row[who+"Targets"]) != 0 {
			return fmt.Errorf("the %s pawn must go straight to the bill: %#v", who, row)
		}
	}
	return nil
}
