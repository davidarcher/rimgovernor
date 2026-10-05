package buildingruntime

import (
	"errors"
	"fmt"
	"log/slog"
	"testing"
	"time"
)

// One planner_step row per Rounder step (#2066): ok when the review ran,
// failed with the error as an attr, reason control_lost for ErrControl.
func TestRoundsStepDecisionShape(t *testing.T) {
	ok := roundsStepDecision(nil, 2*time.Millisecond, true)
	p := ok.Payload()
	if ok.Kind != "planner_step" || p["target"] != "rounds" || p["verdict"] != "ok" || p["reason"] != "reviewed" || ok.Level != slog.LevelInfo || p["attrs"].(map[string]any)["partial"] != true {
		t.Fatalf("%+v", p)
	}
	lost := roundsStepDecision(fmt.Errorf("%w: step", ErrControl), 0, false)
	if p := lost.Payload(); p["verdict"] != "failed" || p["reason"] != "control_lost" || lost.Level != slog.LevelWarn || p["attrs"].(map[string]any)["error"] == nil {
		t.Fatalf("%+v", p)
	}
	failed := roundsStepDecision(errors.New("boom"), 0, false)
	if p := failed.Payload(); p["reason"] != "error" || p["attrs"].(map[string]any)["error"] != "boom" {
		t.Fatalf("%+v", p)
	}
}

func TestLayoutPlanDecisionVerdicts(t *testing.T) {
	d := layoutPlanDecision("replanned", "outgrown", 7, "plan", map[string]any{"unplaced": "x"})
	p := d.Payload()
	attrs := p["attrs"].(map[string]any)
	if d.Kind != "layout_plan" || p["verdict"] != "replanned" || p["reason"] != "outgrown" || attrs["colonists"] != 7 || attrs["unplaced"] != "x" {
		t.Fatalf("%+v", p)
	}
}
