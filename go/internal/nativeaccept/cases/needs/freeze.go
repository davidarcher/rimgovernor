// The needs/freeze case proves test/freeze_needs (issue #92): with every need
// but Rest frozen, a tenth of a day at accelerated Ultrafast leaves each free colonist's
// frozen needs at maximum while Rest keeps moving; release lets them fall
// again.
package needs

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// kept is the one need the session leaves live; the freeze itself is
// what the case proves.
const kept = "Rest"

func init() {
	cases.Register(cases.Case{
		Name: "needs/freeze",
		Scope: "test/freeze_needs pins every free colonist need but the kept ones at maximum across " +
			"an advance window and releases them on request.",
		Start:  cases.DebugStart{},
		Keep:   []string{kept},
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	identity := s.Identity()
	names := s.Names()
	report["frozen"] = report["frozen_needs"]
	supervisor := &na.ScenarioClock{Wire: h.WireFunc(), Identity: identity, Owner: na.Controller, Report: report, TestAcceleration: true}
	if _, err := supervisor.Acquire(ctx, "acquire"); err != nil {
		return err
	}
	rt := &na.ScenarioRuntime{Query: h.Call, Clock: supervisor, Report: report, Tools: names}
	levels := func(label string) (map[string]map[string]float64, error) {
		reply, err := h.Call(ctx, label, na.FreezeNeedsTool, map[string]any{"action": "inspect"})
		if err != nil {
			return nil, err
		}
		out := map[string]map[string]float64{}
		for _, raw := range na.AsSlice(reply["needs"]) {
			row, _ := na.AsMap(raw)
			pawn := na.AsString(row["pawn"])
			out[pawn] = map[string]float64{}
			for _, n := range na.AsSlice(row["needs"]) {
				need, _ := na.AsMap(n)
				out[pawn][na.AsString(need["def"])] = na.AsNumber(need["level"])
			}
		}
		if len(out) == 0 {
			return nil, fmt.Errorf("%s: no free colonists reported: %#v", label, reply)
		}
		return out, nil
	}
	before, err := levels("needs-before")
	if err != nil {
		return err
	}
	// A tenth of a day: Rest falls by a few hundredths awake, more than
	// enough to show it is live, while a frozen need would fall the same.
	if _, err := na.AdvanceGame(ctx, rt, 6000, na.WithTimeout(180*time.Second)); err != nil {
		return fmt.Errorf("frozen window: %w", err)
	}
	after, err := levels("needs-after")
	if err != nil {
		return err
	}
	summary := map[string]any{"kept": kept}
	report["needs"] = summary
	moved := 0
	for pawn, needs := range after {
		for def, level := range needs {
			if def == kept {
				if level != before[pawn][def] {
					moved++
				}
				continue
			}
			if level < 0.95 {
				return fmt.Errorf("frozen need %s on %s fell to %.3f", def, pawn, level)
			}
		}
	}
	if moved == 0 {
		return fmt.Errorf("kept need %s did not move on any colonist: %#v", kept, after)
	}
	summary["kept_moved_on"] = moved
	summary["after"] = after

	released, err := h.Call(ctx, "release", na.FreezeNeedsTool, map[string]any{"action": "release"})
	if err != nil {
		return err
	}
	if frozen, _ := na.AsBool(released["frozen"]); frozen {
		return fmt.Errorf("release left needs frozen: %#v", released)
	}
	if _, err := na.AdvanceGame(ctx, rt, 6000, na.WithTimeout(180*time.Second)); err != nil {
		return fmt.Errorf("released window: %w", err)
	}
	final, err := levels("needs-released")
	if err != nil {
		return err
	}
	fell := 0
	for pawn, needs := range final {
		for def, level := range needs {
			if def != kept && level < after[pawn][def] {
				fell++
			}
		}
	}
	if fell == 0 {
		return fmt.Errorf("no need fell after release: %#v", final)
	}
	summary["fell_after_release"] = fell
	return nil
}
