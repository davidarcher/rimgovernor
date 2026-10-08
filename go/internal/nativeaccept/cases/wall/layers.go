package wall

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:  "wall/layers",
		Scope: "All three layers of a funded wall ring start as standable frames and finish through native pawn work without stranded frames or trapped colonists (#2314). A completion that seals an unfinished neighbour is refused; a separate pawn-on-frame completion records vanilla relocation and requires escape. A Go snapshot cannot prove frame standability, Harmony completion vetoes or vanilla pawn displacement.",
		Start: cases.LabStart(), RequiredOps: []string{"test/wall_layers"},
		Budget: 4 * time.Minute, Crew: cases.Crew{Size: 3},
		Run: runWallLayers,
	})
}

func runWallLayers(ctx context.Context, s cases.Session) error {
	h := s.Harness()
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "wall-layer-authority", s.Identity()); err != nil {
		return err
	}
	call := func(label, action string) (map[string]any, error) {
		row, err := h.Call(ctx, label, "test/wall_layers", map[string]any{"action": action})
		if err != nil {
			return nil, err
		}
		if ok, _ := na.AsBool(row["success"]); !ok {
			return nil, fmt.Errorf("wall_layers %s refused: %#v", action, row)
		}
		return row, nil
	}
	before, err := call("fund-all-layers", "prepare")
	if err != nil {
		return err
	}
	expected := na.AsNumber(before["expected"])
	if expected != 69 || na.AsNumber(before["frames"]) != expected || na.AsNumber(before["funded"]) != expected || na.AsNumber(before["standable"]) != expected || na.AsNumber(before["standing"]) != 0 {
		return fmt.Errorf("all layers must start funded, standable and unfinished: %#v", before)
	}
	s.Report()["precondition"] = before
	for ticks := 0; ticks <= 24000; ticks += 300 {
		row, err := call(fmt.Sprintf("layers-%d", ticks), "audit")
		if err != nil {
			return err
		}
		if na.AsNumber(row["trapped"]) != 0 {
			return fmt.Errorf("wall ring trapped a colonist: %#v", row)
		}
		if na.AsNumber(row["standing"]) == expected && na.AsNumber(row["frames"]) == 0 {
			s.Report()["completed"] = row
			s.Report()["ticks"] = ticks
			controls, err := call("completion-controls", "controls")
			if err != nil {
				return err
			}
			s.Report()["completion_controls"] = controls
			for _, field := range []string{"refused", "skipped", "completed", "escaped"} {
				if ok, _ := na.AsBool(controls[field]); !ok {
					return fmt.Errorf("completion control %s failed: %#v", field, controls)
				}
			}
			return nil
		}
		if ticks == 24000 {
			break
		}
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
	}
	return fmt.Errorf("three-thick ring did not finish within 24000 ticks")
}
