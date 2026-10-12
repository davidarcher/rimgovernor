package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"testing"
	"time"
)

// One planner_step row per Rounder step: ok when the review ran,
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

// A round cancelled by an authority change stops: its planner_step row is an
// info ok/cancelled row, not a failed row with the error attached.
func TestRoundsStepDecisionCancelledIsNotAFailure(t *testing.T) {
	d := roundsStepDecision(fmt.Errorf("routine: %w", context.Canceled), 0, true)
	p := d.Payload()
	if p["verdict"] != "ok" || p["reason"] != "cancelled" || d.Level != slog.LevelInfo {
		t.Fatalf("%+v", p)
	}
	if _, has := p["attrs"].(map[string]any)["error"]; has {
		t.Fatalf("cancelled round recorded an error: %+v", p)
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
